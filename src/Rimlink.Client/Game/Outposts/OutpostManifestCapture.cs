using System.Security.Cryptography;
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
        var (packs, settings) = ReadDefinitionInputs();
        return OutpostManifestClient.DefinitionFingerprint(packs, settings, legacy: true);
    }

    private static (OutpostManifestClient.DefinitionPack[] Packs, List<ModSettingsHash> Settings) ReadDefinitionInputs()
    {
        if (!ModpackActions.Content.Ready.IsCompletedSuccessfully) throw new InvalidDataException("gameplay content fingerprints are not ready");
        var content = ModpackActions.Content.Ready.GetAwaiter().GetResult();
        var packs = LoadedModManager.RunningMods.Select((mod, position) =>
        {
            string package = ModpackActions.Normalize(mod.PackageId);
            if (!content.TryGetValue(package, out var info)) throw new InvalidDataException("a gameplay content fingerprint is missing");
            var defs = mod.AllDefs.Select(d => d.GetType().FullName + ":" + d.defName).Order(StringComparer.Ordinal).ToArray();
            return new OutpostManifestClient.DefinitionPack(position, package, info.FilesHash, defs);
        }).ToArray();
        return (packs, ModpackActions.RunningModSettings());
    }
    public static WorldParams LocalParams(World world)
    {
        var actual = WorldSetup.CollectParams(world).Params;
        var saved = Current.Game?.GetComponent<RimlinkGameComponent>().OutpostWorldGenerationParams;
        var recorded = string.IsNullOrEmpty(saved) ? null : WorldParams.Parser.ParseFrom(Convert.FromBase64String(saved));
        return OutpostManifestClient.BindWorldParameters(actual, recorded, RimlinkSession.World?.Params);
    }
    public static OutpostManifestDocument Capture(World world)
    {
        if (!PawnCustodyRegistry.IsOwnGame || world != Find.World || world.grid.TilesCount is < 1 or > MaxTiles)
            throw new InvalidDataException("only the complete own main planet can be exported");
        var parameters = LocalParams(world);
        var (packs, settings) = ReadDefinitionInputs();
        var document = new OutpostManifestDocument { SchemaVersion = 1, Binding = new()
            { WorldInstanceId = RimlinkSession.World?.WorldInstanceId ?? "", WorldParamsSha256 = ByteString.CopyFrom(SHA256.HashData(parameters.ToByteArray())) },
            GameVersion = VersionControl.CurrentVersionString, MainSurfaceId = "planet",
            GameDefsSha256 = OutpostManifestClient.DefinitionFingerprint(packs, settings, legacy: true),
            GameplayDefsSha256 = OutpostManifestClient.DefinitionFingerprint(packs, settings, legacy: false),
            TileCount = world.grid.TilesCount, ValuationVersion = "game-base-market-v1" };
        document.ActiveDlc.Add(ActiveDlc());
        for (int i = 0; i < document.TileCount; i++)
        {
            var tile = world.grid[i]; var biome = tile.PrimaryBiome ?? throw new InvalidDataException("tile biome is unavailable");
            bool land = !biome.isWaterBiome && !biome.inVacuum;
            document.Tiles.Add(new OutpostManifestTile { Tile = i, SurfaceId = "planet", BiomeDef = biome.defName,
                Land = land, CaravanPassable = land && world.pathGrid.Passable(new PlanetTile(i, world.grid.Surface)), NaturalTrees = biome.TreeDensity > 0 });
        }
        foreach (var def in new[] { ThingDefOf.Steel, ThingDefOf.WoodLog })
            document.Resources.Add(new OutpostResourceValuation { DefName = def.defName, UnitValueX100 = checked((long)Math.Round(def.BaseMarketValue * 100d, MidpointRounding.AwayFromZero)) });
        if (document.CalculateSize() > OutpostManifestClient.MaxDocumentBytes) throw new InvalidDataException("complete outpost geography exceeds the bounded export size");
        return document;
    }
    public static void RequireLocalMatch(OutpostManifestDocument manifest)
    {
        var (packs, settings) = ReadDefinitionInputs();
        OutpostManifestClient.RequireLocalMatch(manifest, LocalParams(Find.World), RimlinkSession.World?.WorldInstanceId ?? "",
            VersionControl.CurrentVersionString, ActiveDlc(),
            OutpostManifestClient.DefinitionFingerprint(packs, settings, legacy: manifest.GameplayDefsSha256.IsEmpty));
    }
}
