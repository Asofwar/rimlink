using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Outposts;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;
using World = RimWorld.Planet.World;

namespace Rimlink.Client.Game.Outposts;

/// <summary>Trusted administrator export of actual geography; no map generation, seed-only inference or server-side game objects.</summary>
public static class OutpostManifestCapture
{
    public const int MaxTiles = 2_000_000;
    public static string[] ActiveDlc() => LoadedModManager.RunningMods.Where(m => m.IsOfficialMod && !m.IsCoreMod)
        .Select(m => ModpackActions.Normalize(m.PackageId)).Order(StringComparer.Ordinal).ToArray();
    public static ByteString DefsFingerprint()
    {
        if (!ModpackActions.Content.Ready.IsCompletedSuccessfully) throw new InvalidDataException("gameplay content fingerprints are not ready");
        var content = ModpackActions.Content.Ready.GetAwaiter().GetResult();
        var packs = LoadedModManager.RunningMods.Select((m, position) => new
        {
            Position = position, Package = ModpackActions.Normalize(m.PackageId),
            Content = content.TryGetValue(ModpackActions.Normalize(m.PackageId), out var info) ? info.FilesHash
                : throw new InvalidDataException("a gameplay content fingerprint is missing"),
            Defs = m.AllDefs.Select(d => d.GetType().FullName + ":" + d.defName).Order(StringComparer.Ordinal).ToArray(),
        }).ToArray();
        // Full Defs/Patches/Assemblies source bytes + load order + actual loaded def identities + active settings.
        // This is compatibility provenance, not an anti-cheat assertion about arbitrary runtime code.
        var settings = ModpackActions.RunningModSettings().OrderBy(s => s.PackageId, StringComparer.Ordinal).ThenBy(s => s.Handle, StringComparer.Ordinal)
            .Select(s => new { s.PackageId, s.Handle, s.Hash }).ToArray();
        return ByteString.CopyFrom(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Format = "outpost-game-defs-v1", Packs = packs, Settings = settings })));
    }
    public static WorldParams LocalParams(World world)
    {
        var actual = WorldSetup.CollectParams(world).Params;
        var saved = Current.Game?.GetComponent<RimlinkGameComponent>().OutpostWorldGenerationParams;
        if (string.IsNullOrEmpty(saved)) return actual;
        var original = WorldParams.Parser.ParseFrom(Convert.FromBase64String(saved));
        // Storyteller/scenario can subsequently change; the actual planet inputs must still match the recorded generation.
        var planet = actual.Clone(); planet.ScenarioDef = original.ScenarioDef; planet.StorytellerDef = original.StorytellerDef;
        planet.DifficultyDef = original.DifficultyDef; planet.DifficultyValues = original.DifficultyValues;
        if (!planet.Equals(original)) throw new InvalidDataException("loaded planet differs from its generation fingerprint");
        return original;
    }
    public static OutpostManifestDocument Capture(World world)
    {
        if (!PawnCustodyRegistry.IsOwnGame || world != Find.World || world.grid.TilesCount is < 1 or > MaxTiles)
            throw new InvalidDataException("only the complete own main planet can be exported");
        var parameters = LocalParams(world);
        var document = new OutpostManifestDocument { SchemaVersion = 1, Binding = new()
            { WorldInstanceId = RimlinkSession.World?.WorldInstanceId ?? "", WorldParamsSha256 = ByteString.CopyFrom(SHA256.HashData(parameters.ToByteArray())) },
            GameVersion = VersionControl.CurrentVersionString, GameDefsSha256 = DefsFingerprint(), MainSurfaceId = "planet",
            TileCount = world.grid.TilesCount, ValuationVersion = "game-base-market-v1" };
        document.ActiveDlc.Add(ActiveDlc());
        for (int i = 0; i < document.TileCount; i++)
        {
            var tile = world.grid[i]; var biome = tile.PrimaryBiome ?? throw new InvalidDataException("tile biome is unavailable");
            bool land = !biome.isWaterBiome && !biome.inVacuum;
            document.Tiles.Add(new OutpostManifestTile { Tile = i, SurfaceId = "planet", BiomeDef = biome.defName,
                Land = land, CaravanPassable = land && world.pathGrid.Passable(new PlanetTile(i, world.grid.Surface)), NaturalTrees = biome.TreeDensity > 0 });
        }
        foreach (var def in new[] { ThingDefOf.WoodLog, ThingDefOf.Steel })
            document.Resources.Add(new OutpostResourceValuation { DefName = def.defName, UnitValueX100 = checked((long)Math.Round(def.BaseMarketValue * 100d, MidpointRounding.AwayFromZero)) });
        if (document.CalculateSize() > OutpostManifestClient.MaxDocumentBytes) throw new InvalidDataException("complete outpost geography exceeds the bounded export size");
        return document;
    }
    public static void RequireLocalMatch(OutpostManifestDocument manifest) => OutpostManifestClient.RequireLocalMatch(manifest,
        LocalParams(Find.World), RimlinkSession.World?.WorldInstanceId ?? "", VersionControl.CurrentVersionString, ActiveDlc(), DefsFingerprint());
}
