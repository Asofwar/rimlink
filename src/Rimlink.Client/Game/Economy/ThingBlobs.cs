using Google.Protobuf;
using RimWorld;
using Rimlink.Protocol.V1;
using Verse;
using ZstdSharp;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// Things and pawns as opaque blobs: vanilla <see cref="Scribe"/> XML of the object (every comp, hediff, gene, item of
/// apparel — whatever the mods attached), zstd-compressed, plus a descriptor the server can list and check.
/// Main thread only; <see cref="Scribe"/> writes through a temp file because it only knows file paths.
/// </summary>
public static class ThingBlobs
{
    private const string Root = "rimlink";
    private const string Node = "thing";
    /// <summary>A corpse's pawn, saved beside the corpse: the corpse holds it by reference only (see <see cref="Save"/>).</summary>
    private const string CorpsePawnNode = "corpsePawn";

    public static ItemBlob SerializeItem(Thing t)
    {
        var blob = new ItemBlob
        {
            DefName = t.def.defName,
            StuffDef = t.Stuff?.defName ?? "",
            Count = t.stackCount,
            Quality = t.TryGetQuality(out var quality) ? (int)quality : -1,
            HitPoints = t.def.useHitPoints ? t.HitPoints : 0,
            MaxHitPoints = t.def.useHitPoints ? t.MaxHitPoints : 0,
            MarketValueX100 = (long)(t.MarketValue * 100f),
            Label = t.LabelCapNoCount,
            ScribeXmlZstd = ByteString.CopyFrom(Save(t)),
        };
        return blob;
    }

    public static PawnBlob SerializePawn(Pawn p)
    {
        var blob = new PawnBlob
        {
            KindDef = p.kindDef.defName,
            Name = p.Name?.ToStringFull ?? p.LabelShort,
            Humanlike = p.RaceProps.Humanlike,
            AgeYears = p.ageTracker.AgeBiologicalYears,
            Label = p.LabelCap,
            MarketValueX100 = (long)(p.MarketValue * 100f),
            ScribeXmlZstd = ByteString.CopyFrom(Save(p)),
        };
        if (p.RaceProps.Humanlike && p.skills is not null)
        {
            foreach (var s in p.skills.skills) blob.Skills.Add(new SkillBrief { SkillDef = s.def.defName, Level = s.Level });
        }
        return blob;
    }

    /// <param name="newIds">True for an object from another game: its thing and hediff IDs may collide with ours.</param>
    public static Thing DeserializeItem(ItemBlob b, bool newIds = false)
    {
        var thing = Load<Thing>(b.ScribeXmlZstd.ToByteArray());
        if (newIds) RegenerateIds(thing);
        AdoptCorpsePawn(thing);
        return thing;
    }

    /// <summary>
    /// The game keeps a corpse's dead pawn among the world pawns, which is where a save writes it: an arriving corpse's pawn goes
    /// there too, or the next save writes the corpse without it. A dead pawn already there under the same id (the original of a
    /// corpse coming back) gives way to the arriving one; an alive one is left alone.
    /// </summary>
    private static void AdoptCorpsePawn(Thing thing)
    {
        if (thing is not Corpse { InnerPawn: { } inner } || Find.WorldPawns is not { } world) return;
        if (world.Contains(inner))
        {
            var stored = world.AllPawnsDead.FirstOrDefault(p => p.thingIDNumber == inner.thingIDNumber);
            if (stored is null || ReferenceEquals(stored, inner)) return;
            world.RemovePawn(stored);
        }
        world.PassToWorld(inner);
    }

    /// <summary>A pawn exactly as serialised (same ids, faction untouched): for the live mirror, where the host's ids and factions are the truth.</summary>
    public static Pawn DeserializePawnRaw(PawnBlob b) => Load<Pawn>(b.ScribeXmlZstd.ToByteArray());

    /// <summary>A thing serialised while spawned remembers its map; it must forget it before it is spawned on the mirror.</summary>
    public static void ResetMapIndex(Thing t) => HarmonyLib.Traverse.Create(t).Field("mapIndexOrState").SetValue((sbyte)-1);

    /// <summary>The ideo the game's load fallback gives a pawn being deserialised (see <see cref="Patches.Patch_Pawn_IdeoTracker_FallbackIdeo"/>); null outside <see cref="DeserializePawn"/>.</summary>
    public static Ideo? ArrivalIdeo { get; private set; }

    /// <param name="joins">The faction the pawn arrives in (the player's by default); the pawn also takes its primary ideo.</param>
    public static Pawn DeserializePawn(PawnBlob b, bool newIds = false, Faction? joins = null)
    {
        joins ??= Faction.OfPlayer;
        // The pawn's ideo reference never resolves (no ideo travels in a blob), so the game assigns a fallback while loading: the primary ideo
        // of whichever local faction carries the sender's faction id, or a random one. Setting it afterwards would record an ideo change
        // (apostasy thoughts) and leave the fallback in the pawn's previous ideos, so the fallback itself is pointed at the joined faction's.
        ArrivalIdeo = joins.ideos?.PrimaryIdeo;
        Pawn pawn;
        try
        {
            pawn = Load<Pawn>(b.ScribeXmlZstd.ToByteArray());
        }
        finally
        {
            ArrivalIdeo = null;
        }
        if (newIds) RegenerateIds(pawn);
        ForgetJobs(pawn);
        if (pawn.Faction != joins) pawn.SetFaction(joins);
        if (pawn.RaceProps.Humanlike) PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn);
        // References into the sender's game (their policies) did not travel either and resolve to null: point them at ours.
        if (pawn.outfits is not null && pawn.outfits.CurrentApparelPolicy is null) pawn.outfits.CurrentApparelPolicy = Current.Game.outfitDatabase.DefaultOutfit();
        if (pawn.drugs is not null && pawn.drugs.CurrentPolicy is null) pawn.drugs.CurrentPolicy = Current.Game.drugPolicyDatabase.DefaultDrugPolicy();
        if (pawn.foodRestriction is not null && pawn.foodRestriction.CurrentFoodPolicy is null) pawn.foodRestriction.CurrentFoodPolicy = Current.Game.foodRestrictionDatabase.DefaultFoodRestriction();
        return pawn;
    }

    /// <summary>
    /// A pawn serialised while spawned carries the job it had on the sender's map (a raider's or a guest's <c>Wait</c>). Unspawned pawns
    /// still tick their job (a drop pod does), and <c>JobDriver_Wait</c> reads <c>pawn.Map</c>, which is null there: the pawn arrives
    /// without a job, as vanilla's <c>DeSpawn</c> leaves it. Dropped by assignment: <c>StopAll</c> would drop carried things and release
    /// reservations on maps of this game the job never had.
    /// </summary>
    private static void ForgetJobs(Pawn pawn)
    {
        if (pawn.jobs is not { } jobs) return;
        jobs.ClearDriver();
        jobs.curJob = null;
        jobs.jobQueue = new Verse.AI.JobQueue();
        if (!pawn.Downed) jobs.posture = PawnPosture.Standing;
    }

    /// <summary>
    /// New thing IDs for the object and everything it holds (apparel, equipment, inventory, carried, and the filth a pawn carries on
    /// its feet, which is deep-saved outside any thing owner), new hediff IDs for pawns.
    /// </summary>
    private static void RegenerateIds(Thing root)
    {
        var held = new List<Thing>();
        if (root is IThingHolder holder) ThingOwnerUtility.GetAllThingsRecursively(holder, held); // clears the list first
        foreach (var p in held.Prepend(root).OfType<Pawn>().ToList())
            if (p.filth?.CarriedFilthListForReading is { } filth) held.AddRange(filth);
        foreach (var t in held.Prepend(root).Distinct())
        {
            if (t.def.HasThingIDNumber) t.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();
            // Verb load ids embed their owner's id ("Thing_Human12_0_Smash"): renamed for the new ids, or the next load replaces
            // them ("Replaced verb") and two copies of one pawn collide in the loaded object directory. Renamed, not rebuilt: the
            // verbs keep their caster (a weapon's is set only when it is equipped or loaded), an ability's back reference, and the
            // jobs and stances that point at them.
            if (t is ThingWithComps withComps)
                foreach (var comp in withComps.AllComps) if (comp is IVerbOwner owner) RenameVerbs(owner);
            if (t is not Pawn p) continue;
            RenameOwnVerbs(p);
            if (p.health?.hediffSet?.hediffs is { } hediffs)
                foreach (var h in hediffs)
                {
                    h.loadID = Find.UniqueIDsManager.GetNextHediffID();
                    if (h is HediffWithComps withHediffComps)
                        foreach (var comp in withHediffComps.comps) if (comp is IVerbOwner owner) RenameVerbs(owner);
                }
            if (p.genes?.GenesListForReading is { } genes)
                foreach (var g in genes) g.loadID = Find.UniqueIDsManager.GetNextGeneID();
            if (p.abilities?.abilities is { } abilities)
                foreach (var a in abilities) { a.Id = Find.UniqueIDsManager.GetNextAbilityID(); RenameVerbs(a); }
        }
    }

    /// <summary>The pawn's own verbs (body, natives, terrain melee, mutant) renamed after its thing id changed (their load ids embed it).</summary>
    public static void RenameOwnVerbs(Pawn pawn)
    {
        RenameVerbs(pawn);
        if (pawn.natives is IVerbOwner natives) RenameVerbs(natives);
        if (pawn.mutant is IVerbOwner mutant) RenameVerbs(mutant);
        if (pawn.meleeVerbs is { } melee && TerrainVerbs(melee) is IVerbOwner terrain) RenameVerbs(terrain);
    }

    private static readonly HarmonyLib.AccessTools.FieldRef<VerbTracker, List<Verb>> TrackerVerbs =
        HarmonyLib.AccessTools.FieldRefAccess<VerbTracker, List<Verb>>("verbs");

    private static readonly HarmonyLib.AccessTools.FieldRef<Pawn_MeleeVerbs, Pawn_MeleeVerbs_TerrainSource> TerrainVerbs =
        HarmonyLib.AccessTools.FieldRefAccess<Pawn_MeleeVerbs, Pawn_MeleeVerbs_TerrainSource>("terrainVerbs");

    /// <summary>The owner's existing verbs take the load ids its VerbTracker would build now; verbs not made yet are left to it.</summary>
    private static void RenameVerbs(IVerbOwner owner)
    {
        if (owner.VerbTracker is not { } tracker || TrackerVerbs(tracker) is not { } verbs) return;
        foreach (var v in verbs)
        {
            if (v.tool is not null) v.loadID = Verb.CalculateUniqueLoadID(owner, v.tool, v.maneuver);
            else if (owner.VerbProperties?.IndexOf(v.verbProps) is >= 0 and var index) v.loadID = Verb.CalculateUniqueLoadID(owner, index);
        }
    }

    public static int SizeOf(ItemList? items, PawnList? pawns) =>
        (items?.Items.Sum(i => i.ScribeXmlZstd.Length) ?? 0) + (pawns?.Pawns.Sum(p => p.ScribeXmlZstd.Length) ?? 0);

    /// <summary>Cross-reference warnings swallowed in the current blob pass, by xml node (save) or referenced type (load); null outside a pass.</summary>
    [ThreadStatic] private static Dictionary<string, int>? crossRefs;

    /// <summary>
    /// True for Scribe's warnings about references leaving the blob, while a blob is saved or loaded: on save "Object with load
    /// ID … is referenced … but is not deep-saved" (dev mode only), on load "Could not resolve reference to …". Counted for the
    /// pass's summary line; everything else is logged as usual.
    /// </summary>
    public static bool SwallowCrossRefWarning(string text)
    {
        if (crossRefs is null || text is null || CrossRefKey(text) is not { } key) return false;
        crossRefs[key] = crossRefs.GetValueOrDefault(key) + 1;
        return true;
    }

    private static string? CrossRefKey(string text)
    {
        const string NotDeepSaved = "Object with load ID ", NodeName = " is referenced (xml node name: ";
        const string Unresolved = "Could not resolve reference to object with loadID ", OfType = " of type ";
        if (text.StartsWith(NotDeepSaved, StringComparison.Ordinal) && text.IndexOf(NodeName, StringComparison.Ordinal) is var n and >= 0)
        {
            int start = n + NodeName.Length, end = text.IndexOf(')', start);
            return end > start ? text[start..end] : "?";
        }
        if (text.StartsWith(Unresolved, StringComparison.Ordinal))
        {
            int t = text.IndexOf(OfType, StringComparison.Ordinal), start = t + OfType.Length, end = t < 0 ? -1 : text.IndexOf(". ", start, StringComparison.Ordinal);
            if (end <= start) return "?";
            string type = text[start..end];
            return type[(type.LastIndexOf('.') + 1)..];
        }
        if (text.StartsWith("Could not resolve reference to Thing with loadID ", StringComparison.Ordinal)) return "Thing (by id)"; // resolved by thing id after all
        return null;
    }

    private static void BeginCrossRefs() => crossRefs = [];

    /// <summary>The last summary per blob pass ("blob loaded Human12"): a live mirror reloads the same pawns every refresh.</summary>
    private static readonly Dictionary<string, string> lastSummary = [];

    /// <summary>Ends the pass and logs one line when anything was swallowed and it differs from this thing's previous line.</summary>
    private static void EndCrossRefs(string what)
    {
        var counted = crossRefs;
        crossRefs = null;
        if (counted is not { Count: > 0 }) return;
        string details = string.Join(", ", counted.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}"));
        if (lastSummary.TryGetValue(what, out var previous) && previous == details) return;
        if (lastSummary.Count >= 1000) lastSummary.Clear();
        lastSummary[what] = details;
        RimlinkLog.Instance.Info($"{what}: {counted.Values.Sum()} references outside the blob ({details})");
    }

    private static byte[] Save(Thing thing)
    {
        string path = TempPath();
        try
        {
            Scribe.saver.InitSaving(path, Root);
            BeginCrossRefs();
            try
            {
                Scribe_Deep.Look(ref thing, saveDestroyedThings: true, Node);
                // A corpse's container holds its pawn by reference (the game deep-saves dead pawns among the world pawns): alone, the
                // corpse arrives empty ("spawned in bugged state"), so its pawn travels beside it.
                if (thing is Corpse { InnerPawn: { } inner }) Scribe_Deep.Look(ref inner, saveDestroyedThings: true, CorpsePawnNode);
            }
            finally
            {
                try { Scribe.saver.FinalizeSaving(); } // reports the references not saved with the blob
                finally { EndCrossRefs($"blob saved {thing.ThingID}"); }
            }
            using var compressor = new Compressor(3);
            return compressor.Wrap(File.ReadAllBytes(path)).ToArray();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static T Load<T>(byte[] zstd) where T : Thing
    {
        string path = TempPath();
        try
        {
            using (var decompressor = new Decompressor())
            {
                File.WriteAllBytes(path, decompressor.Unwrap(zstd).ToArray());
            }
            T? thing = null;
            Scribe.loader.InitLoading(path);
            BeginCrossRefs();
            try
            {
                Scribe_Deep.Look(ref thing, saveDestroyedThings: true, Node);
                if (thing is Corpse)
                {
                    // Loaded into this pass's object directory, so the corpse's reference resolves to it (older blobs have no such node).
                    Pawn? corpsePawn = null;
                    Scribe_Deep.Look(ref corpsePawn, saveDestroyedThings: true, CorpsePawnNode);
                }
                RegisterWorldFactions();
            }
            finally
            {
                // Resolves references inside the blob and runs PostLoadInit; references to things outside it are dropped (counted, one summary line).
                try { Scribe.loader.FinalizeLoading(); }
                finally { EndCrossRefs($"blob loaded {(thing?.def is null ? typeof(T).Name : thing.ThingID)}"); }
            }
            return thing ?? throw new InvalidDataException("blob did not contain a " + typeof(T).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly HarmonyLib.AccessTools.FieldRef<CrossRefHandler, LoadedObjectDirectory> LoadedObjects =
        HarmonyLib.AccessTools.FieldRefAccess<CrossRefHandler, LoadedObjectDirectory>("loadedObjectDirectory");

    /// <summary>
    /// Faction references in a blob (royal titles, favor, heirs) resolve by load id against this world's factions, as the game
    /// already does for the thing's own faction: all players share one world, so its factions carry the same ids. Unresolved, they
    /// load as null and vanilla's royalty code throws on them at every load.
    /// </summary>
    private static void RegisterWorldFactions()
    {
        if (Find.FactionManager is not { } factions) return;
        var directory = LoadedObjects(Scribe.loader.crossRefs);
        foreach (var faction in factions.AllFactionsListForReading) directory.RegisterLoaded(faction);
    }

    private static string TempPath()
    {
        string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "Rimlink", "blobs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".xml");
    }
}
