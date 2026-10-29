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
        return thing;
    }

    /// <summary>A pawn exactly as serialised (same ids, faction untouched): for the live mirror, where the host's ids and factions are the truth.</summary>
    public static Pawn DeserializePawnRaw(PawnBlob b) => Load<Pawn>(b.ScribeXmlZstd.ToByteArray());

    /// <summary>A thing serialised while spawned remembers its map; it must forget it before it is spawned on the mirror.</summary>
    public static void ResetMapIndex(Thing t) => HarmonyLib.Traverse.Create(t).Field("mapIndexOrState").SetValue((sbyte)-1);

    public static Pawn DeserializePawn(PawnBlob b, bool newIds = false)
    {
        var pawn = Load<Pawn>(b.ScribeXmlZstd.ToByteArray());
        if (newIds) RegenerateIds(pawn);
        if (pawn.Faction != Faction.OfPlayer) pawn.SetFaction(Faction.OfPlayer);
        if (pawn.RaceProps.Humanlike) PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn);
        // References into the sender's game (their ideo, their policies) did not travel: point them at ours.
        if (pawn.ideo is not null && pawn.Ideo is null && Faction.OfPlayer.ideos?.PrimaryIdeo is { } ideo) pawn.ideo.SetIdeo(ideo);
        if (pawn.outfits is not null && pawn.outfits.CurrentApparelPolicy is null) pawn.outfits.CurrentApparelPolicy = Current.Game.outfitDatabase.DefaultOutfit();
        if (pawn.drugs is not null && pawn.drugs.CurrentPolicy is null) pawn.drugs.CurrentPolicy = Current.Game.drugPolicyDatabase.DefaultDrugPolicy();
        if (pawn.foodRestriction is not null && pawn.foodRestriction.CurrentFoodPolicy is null) pawn.foodRestriction.CurrentFoodPolicy = Current.Game.foodRestrictionDatabase.DefaultFoodRestriction();
        return pawn;
    }

    /// <summary>New thing IDs for the object and everything it holds (apparel, equipment, inventory, carried), new hediff IDs for pawns.</summary>
    private static void RegenerateIds(Thing root)
    {
        var held = new List<Thing>();
        if (root is IThingHolder holder) ThingOwnerUtility.GetAllThingsRecursively(holder, held); // clears the list first
        foreach (var t in held.Prepend(root).Distinct())
        {
            if (t.def.HasThingIDNumber) t.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();
            if (t is not Pawn p) continue;
            if (p.health?.hediffSet?.hediffs is { } hediffs)
                foreach (var h in hediffs) h.loadID = Find.UniqueIDsManager.GetNextHediffID();
            if (p.genes?.GenesListForReading is { } genes)
                foreach (var g in genes) g.loadID = Find.UniqueIDsManager.GetNextGeneID();
            if (p.abilities?.abilities is { } abilities)
                foreach (var a in abilities) a.Id = Find.UniqueIDsManager.GetNextAbilityID();
        }
    }

    public static int SizeOf(ItemList? items, PawnList? pawns) =>
        (items?.Items.Sum(i => i.ScribeXmlZstd.Length) ?? 0) + (pawns?.Pawns.Sum(p => p.ScribeXmlZstd.Length) ?? 0);

    private static byte[] Save(Thing thing)
    {
        string path = TempPath();
        try
        {
            Scribe.saver.InitSaving(path, Root);
            try
            {
                Scribe_Deep.Look(ref thing, saveDestroyedThings: true, Node);
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
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
            try
            {
                Scribe_Deep.Look(ref thing, saveDestroyedThings: true, Node);
            }
            finally
            {
                // Resolves references inside the blob and runs PostLoadInit; references to things outside it are dropped (logged by the game).
                Scribe.loader.FinalizeLoading();
            }
            return thing ?? throw new InvalidDataException("blob did not contain a " + typeof(T).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPath()
    {
        string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "Rimlink", "blobs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".xml");
    }
}
