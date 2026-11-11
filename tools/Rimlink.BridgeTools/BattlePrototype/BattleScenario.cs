using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using RimWorld;
using Rimlink.BattlePrototype;
using Rimlink.Client.Game.Live;
using Rimlink.Client.Game.Raids;
using Verse;
using Verse.AI.Group;

namespace Rimlink.BridgeTools.BattlePrototype;

internal sealed class BattleScenario(BattleBinding binding)
{
    internal Map Map { get; private set; } = null!;
    internal Dictionary<string, Pawn> Pawns { get; } = new(StringComparer.Ordinal);
    internal string InitialSaveName => "Capture-" + binding.Identity.BattleId.ToString("N") + "-initial";
    internal string TemplateSaveName => "Capture-" + binding.Identity.BattleId.ToString("N") + "-template";
    internal BattleArtifact? InitialArtifact { get; private set; }

    internal void LoadTemplate()
    {
        string template = Path.Combine(binding.StandRoot, "templates", "vanilla.rws");
        if (Hash(template) != binding.Identity.TemplateDigest) throw new InvalidDataException("native template changed");
        CopyDurable(template, GenFilePaths.FilePathForSavedGame(TemplateSaveName));
        GlobalTextureAtlasManager.FreeAllRuntimeAtlases(); PortraitsCache.Clear();
        GameDataSaveLoader.LoadGame(TemplateSaveName);
    }

    internal void CreateArena()
    {
        if (Current.ProgramState != ProgramState.Playing || Find.Maps.Count != 1) throw new InvalidDataException("one disposable template map required");
        Map = Find.CurrentMap;
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        var arena = BattleFixture.Arena;
        if (!new IntVec3(arena.MaxX, 0, arena.MaxZ).InBounds(Map)) throw new InvalidDataException("arena does not fit template");
        foreach (var pawn in Map.mapPawns.AllPawnsSpawned.ToArray()) pawn.Destroy(DestroyMode.Vanish);
        foreach (var thing in Map.listerThings.AllThings.ToArray())
            if (thing.Position.x >= arena.MinX && thing.Position.x <= arena.MaxX && thing.Position.z >= arena.MinZ && thing.Position.z <= arena.MaxZ)
            {
                if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                else thing.DeSpawn();
            }
        var concrete = DefDatabase<TerrainDef>.GetNamed("Concrete");
        for (int x = arena.MinX; x <= arena.MaxX; x++)
            for (int z = arena.MinZ; z <= arena.MaxZ; z++)
            {
                var cell = new IntVec3(x, 0, z);
                Map.roofGrid.SetRoof(cell, null);
                Map.terrainGrid.SetTerrain(cell, concrete);
                Map.snowGrid.SetDepth(cell, 0);
                if (x == arena.MinX || x == arena.MaxX || z == arena.MinZ || z == arena.MaxZ)
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, DefDatabase<ThingDef>.GetNamed("BlocksGranite")), cell, Map);
            }
        var attacker = RimlinkFactions.Raiders;
        var defender = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(attacker.def, default, hidden: true));
        defender.Name = "Battle defenders";
        Find.FactionManager.Add(defender);
        attacker.SetRelationDirect(defender, FactionRelationKind.Hostile, canSendHostilityLetter: false);
        foreach (var spec in BattleFixture.Roster())
        {
            var faction = spec.Side == BattleSide.Attacker ? attacker : defender;
            var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Colonist"), faction,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: true,
                allowAddictions: false, fixedBiologicalAge: 25, fixedChronologicalAge: 25, fixedGender: Gender.Male,
                forceNoIdeo: true, forceNoBackstory: true, forbidAnyTitle: true, forceNoGear: true));
            pawn.Name = new NameSingle(spec.Id);
            foreach (var hediff in pawn.health.hediffSet.hediffs.ToArray()) pawn.health.RemoveHediff(hediff);
            pawn.story.traits.allTraits.Clear(); pawn.story.bodyType = BodyTypeDefOf.Male;
            foreach (var skill in pawn.skills.skills) { skill.Level = 8; skill.passion = Passion.None; skill.xpSinceLastLevel = 0; }
            pawn.equipment.DestroyAllEquipment(); pawn.apparel.DestroyAll(); pawn.inventory.DestroyAll();
            var weapon = (ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
            weapon.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Outsider);
            pawn.equipment.AddEquipment(weapon);
            foreach (string apparelDef in new[] { "Apparel_Pants", "Apparel_BasicShirt" })
            {
                var apparel = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(apparelDef), ThingDefOf.Cloth);
                apparel.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Outsider);
                pawn.apparel.Wear(apparel);
            }
            GenSpawn.Spawn(pawn, new IntVec3(spec.X, 0, spec.Z), Map);
            if (pawn.drafter is not null) pawn.drafter.Drafted = true;
            Pawns.Add(spec.Id, pawn);
        }
        foreach (var faction in new[] { attacker, defender })
            LordMaker.MakeNewLord(faction, new LordJob_RimlinkGuests(), Map, Pawns.Values.Where(p => p.Faction == faction));
        VerifyInitial();
        GameDataSaveLoader.SaveGame(InitialSaveName);
        string file = GenFilePaths.FilePathForSavedGame(InitialSaveName);
        CheckSave(file);
        string initialTarget = BattleFixture.ArtifactPath(binding, BattleArtifactKind.InitialSnapshot);
        if (File.Exists(BattleFixture.FaultPath(BattleFixture.MatchRoot(binding), "before-initial-file-complete")))
        {
            BattleFixture.CheckLocalPath(initialTarget);
            using (var input = File.OpenRead(file))
            using (var output = new FileStream(initialTarget, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                byte[] prefix = new byte[1024]; input.ReadExactly(prefix); output.Write(prefix); output.Flush(flushToDisk: true);
            }
            BattleFixture.CrashIfArmed(BattleFixture.MatchRoot(binding), "before-initial-file-complete");
            throw new InvalidDataException("initial-copy fault was disarmed after partial evidence; recovery required");
        }
        CopyDurable(file, initialTarget);
        InitialArtifact = Describe(BattleArtifactKind.InitialSnapshot);
        GlobalTextureAtlasManager.FreeAllRuntimeAtlases(); PortraitsCache.Clear();
        GameDataSaveLoader.LoadGame(InitialSaveName);
    }

    internal void AttachReloaded()
    {
        Map = Find.CurrentMap; Pawns.Clear();
        foreach (var spec in BattleFixture.Roster())
            Pawns.Add(spec.Id, Map.mapPawns.AllPawnsSpawned.Single(p => p.Name?.ToStringShort == spec.Id));
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        VerifyInitial();
        if (Hash(GenFilePaths.FilePathForSavedGame(InitialSaveName)) != InitialArtifact?.Sha256)
            throw new InvalidDataException("reloaded initial snapshot changed");
    }

    internal BattlePawn[] ReadPawns() => BattleFixture.Roster().Select(spec =>
    {
        var pawn = Pawns[spec.Id];
        var position = pawn.PositionHeld;
        return new BattlePawn(spec.Id, spec.Side, position.x, position.z, !pawn.Dead, pawn.Downed, pawn.InMentalState,
            !pawn.WorkTagIsDisabled(WorkTags.Violent) && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving), pawn.health.summaryHealth.SummaryHealthPercent);
    }).ToArray();

    internal object[] Recipe() => Pawns.Select(pair => (object)new
    {
        id = pair.Key, thingId = pair.Value.ThingID, faction = pair.Value.Faction.Name,
        age = pair.Value.ageTracker.AgeBiologicalYearsFloat, gender = pair.Value.gender.ToString(),
        health = pair.Value.health.summaryHealth.SummaryHealthPercent,
        traits = pair.Value.story.traits.allTraits.Select(t => t.def.defName).ToArray(),
        skills = pair.Value.skills.skills.Select(s => new { def = s.def.defName, level = s.Level }).ToArray(),
        weapon = pair.Value.equipment.Primary?.def.defName,
        apparel = pair.Value.apparel.WornApparel.Select(a => a.def.defName).Order().ToArray()
    }).ToArray();

    private void VerifyInitial()
    {
        if (!ReadPawns().SequenceEqual(BattleFixture.Roster()) || Map.mapPawns.AllPawnsSpawned.Count != 8
            || !Pawns["a1"].Faction.HostileTo(Pawns["b1"].Faction)) throw new InvalidDataException("arena roster/health/positions differ");
        string? equalRecipe = null;
        foreach (var pawn in Pawns.Values)
        {
            var recipe = new
            {
                pawn.gender, age = pawn.ageTracker.AgeBiologicalYearsFloat, body = pawn.story.bodyType.defName,
                traits = pawn.story.traits.allTraits.Select(t => t.def.defName).ToArray(),
                skills = pawn.skills.skills.Select(s => new { s.def.defName, s.Level, s.passion }).ToArray(),
                gear = pawn.apparel.WornApparel.Append(pawn.equipment.Primary).Select(t => new
                { t.def.defName, stuff = t.Stuff?.defName, t.HitPoints, quality = t.TryGetComp<CompQuality>()?.Quality }).OrderBy(t => t.defName).ToArray()
            };
            string digest = BattleContract.Digest(recipe);
            equalRecipe ??= digest;
            if (digest != equalRecipe) throw new InvalidDataException("generated fighters have unequal final stats/gear");
        }
    }

    internal BattleArtifact SaveFinal()
    {
        if (Rimlink.Client.Game.RimlinkSession.Active || Rimlink.Client.Game.RimlinkSession.Connection is not null || Find.CurrentMap != Map)
            throw new InvalidDataException("final save requires the unchanged isolated battle map");
        string name = "Capture-" + binding.Identity.BattleId.ToString("N") + "-final";
        string file = GenFilePaths.FilePathForSavedGame(name);
        if (File.Exists(file)) throw new InvalidDataException("final save already exists");
        GameDataSaveLoader.SaveGame(name); CheckSave(file);
        CopyDurable(file, BattleFixture.ArtifactPath(binding, BattleArtifactKind.FinalSnapshot));
        return Describe(BattleArtifactKind.FinalSnapshot);
    }

    private static void CheckSave(string file)
    {
        var doc = XDocument.Load(file);
        if (doc.Root?.Name.LocalName != "savegame" || doc.Root.Element("game") is null) throw new InvalidDataException("native save is incomplete");
    }
    internal BattleArtifact Describe(BattleArtifactKind kind)
    {
        string path = BattleFixture.ArtifactPath(binding, kind);
        return new(kind, new FileInfo(path).Length, Hash(path));
    }
    internal static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    internal static void CopyDurable(string source, string target)
    {
        BattleFixture.CheckLocalPath(source); BattleFixture.CheckLocalPath(target);
        using var input = File.OpenRead(source);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(flushToDisk: true);
    }
}
