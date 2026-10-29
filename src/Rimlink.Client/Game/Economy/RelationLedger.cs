using HarmonyLib;
using RimWorld;
using Rimlink.Client.Relations;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// The relations of this colony's pawns that are away, saved with the game: a pawn that leaves for good is recorded here before
/// vanilla's discard clears its relations on both sides, and gets them back when it comes home, whichever way it comes
/// (put back after a refusal, a parcel, a returned captive). The blob cannot carry them: a blob loads on its own, so its
/// references to other pawns resolve to nothing, and a pawn back from another game was already stripped of them there.
/// Main thread only; does nothing in a visit's game, which is another colony.
/// </summary>
public sealed class RelationLedger : GameComponent
{
    private static readonly AccessTools.FieldRef<Pawn_RelationsTracker, HashSet<Pawn>> WithMe =
        AccessTools.FieldRefAccess<Pawn_RelationsTracker, HashSet<Pawn>>("pawnsWithDirectRelationsWithMe");

    private RelationBook book = new();
    private List<SavedEntry> saved = [];

    public RelationLedger(Verse.Game game) { }

    private static RelationLedger? Instance => Visits.VisitSession.InVisit ? null : Current.Game?.GetComponent<RelationLedger>();

    public override void ExposeData()
    {
        base.ExposeData();
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            var index = Index([]);
            book.Prune(index.ContainsKey);
            saved = book.Entries.Select(SavedEntry.From).ToList();
        }
        Scribe_Collections.Look(ref saved, "rimlinkRelations", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            saved ??= [];
            book = new RelationBook();
            book.Entries.AddRange(saved.Select(s => s.ToEntry()));
        }
    }

    /// <summary>A pawn of the colony is about to leave for good: what it leaves behind, from both sides.</summary>
    public static void Record(Pawn pawn)
    {
        if (Instance is not { } ledger || pawn.relations is null) return;
        try
        {
            var entry = new RelationEntry { Key = KeyOf(pawn), ThingId = pawn.thingIDNumber, LeftTick = Find.TickManager.TicksGame };
            foreach (var r in pawn.relations.DirectRelations)
            {
                if (r.otherPawn is not null) entry.Edges.Add(new RelationEdge(r.def.defName, r.otherPawn.thingIDNumber, r.startTicks, HeldByOther: false));
            }
            // Reflexive relations are in both lists; the leaver's half brings both back.
            foreach (var other in WithMe(pawn.relations).ToList())
            {
                if (other.relations is null) continue;
                foreach (var r in other.relations.DirectRelations)
                {
                    if (r.otherPawn == pawn && !r.def.reflexive) entry.Edges.Add(new RelationEdge(r.def.defName, other.thingIDNumber, r.startTicks, HeldByOther: true));
                }
            }
            ledger.book.Record(entry);
            RimlinkLog.Instance.Info($"relations recorded for {pawn.LabelShort}: {entry.Edges.Count}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Rimlink] relations of {pawn} could not be recorded: {ex}");
        }
    }

    /// <summary>
    /// A pawn of the colony is home, just deserialised and not yet placed anywhere: it takes back its old thing id (nothing has
    /// hashed it yet) and its relations. Returns the pawn, for use inline where pawns are put back one at a time.
    /// </summary>
    public static Pawn Rejoin(Pawn pawn)
    {
        Rejoin([pawn]);
        return pawn;
    }

    /// <summary>
    /// Pawns home together (one parcel, one refused request), none of them placed yet: each is known to the others before any
    /// relation is given back, so a pair that left together is a pair again.
    /// </summary>
    public static void Rejoin(IReadOnlyList<Pawn> pawns)
    {
        if (Instance is not { } ledger || pawns.Count == 0) return;
        try
        {
            // The social tab keeps rows across pawns, adds them by reference and drops them by thing id: a row of the discarded
            // original would stay, "Missing", beside the pawn back under the same id.
            SocialCardUtility.ClearCaches();
            var back = new List<(Pawn Pawn, RelationEntry Entry)>();
            foreach (var pawn in pawns)
            {
                if (pawn.relations is not null && ledger.book.Take(KeyOf(pawn)) is { } entry) back.Add((pawn, entry));
            }
            if (back.Count == 0) return;
            var index = Index(pawns);
            foreach (var (pawn, entry) in back)
            {
                if (pawn.thingIDNumber == entry.ThingId) continue;
                if (index.ContainsKey(entry.ThingId))
                {
                    RimlinkLog.Instance.Warn($"relations: {pawn.LabelShort} keeps id {pawn.thingIDNumber}, its old id {entry.ThingId} is taken");
                    continue;
                }
                index.Remove(pawn.thingIDNumber);
                pawn.thingIDNumber = entry.ThingId;
                ThingBlobs.RebuildOwnVerbs(pawn);
                index[pawn.thingIDNumber] = pawn;
            }
            var world = new VerseWorld(index);
            foreach (var (pawn, entry) in back)
            {
                foreach (var result in ledger.book.Apply(entry, pawn.thingIDNumber, world))
                {
                    string other = index.TryGetValue(result.Edge.OtherId, out var o) ? o.LabelShort : "#" + result.Edge.OtherId;
                    RimlinkLog.Instance.Info($"relations: {pawn.LabelShort} {result.Edge.Def} {other} {result.Outcome}{(result.Error is null ? "" : ": " + result.Error)}");
                }
                DropVirtualParent(entry.ThingId, pawn);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Rimlink] relations could not be given back to {string.Join(", ", pawns)}: {ex}");
        }
    }

    /// <summary>The pawns among <paramref name="things"/> rejoin together; returns the list, for use inline.</summary>
    public static List<Thing> RejoinAll(List<Thing> things)
    {
        Rejoin(things.OfType<Pawn>().ToList());
        return things;
    }

    private static string KeyOf(Pawn pawn) => RelationBook.KeyOf(pawn.def.defName, pawn.gender.ToString(), pawn.ageTracker.BirthAbsTicks, pawn.Name?.ToStringFull ?? "");

    /// <summary>Discarding a parent of several children turned their Parent into a relation with a record of it; the real one is back.</summary>
    private static void DropVirtualParent(int thingId, Pawn parent)
    {
        if (!Find.RelationshipRecords.Records.TryGetValue(thingId, out var record)) return;
        foreach (var child in record.References.ToList())
        {
            if (child.relations is null || !child.relations.DirectRelationExists(PawnRelationDefOf.Parent, parent)) continue;
            child.relations.RemoveRelation(record);
            record.RemoveReference(child);
        }
    }

    /// <summary>Every pawn of the game by thing id, alive or dead, the dead in corpses and graves included, and those coming home.</summary>
    private static Dictionary<int, Pawn> Index(IReadOnlyList<Pawn> returning)
    {
        var index = new Dictionary<int, Pawn>();
        foreach (var p in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead) index.TryAdd(p.thingIDNumber, p);
        var corpses = new List<Corpse>();
        foreach (var map in Find.Maps)
        {
            ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForGroup(ThingRequestGroup.Corpse), corpses);
            foreach (var corpse in corpses)
            {
                if (corpse.InnerPawn is { } inner) index.TryAdd(inner.thingIDNumber, inner);
            }
        }
        foreach (var p in returning) index[p.thingIDNumber] = p;
        return index;
    }

    /// <summary>The book's view of the game.</summary>
    private sealed class VerseWorld(Dictionary<int, Pawn> pawns) : IRelationWorld
    {
        public bool PawnExists(int id) => pawns.ContainsKey(id);

        public bool DefExists(string def) => Def(def) is not null;

        public bool HasRelation(int holderId, string def, int otherId) => pawns[holderId].relations?.DirectRelationExists(Def(def), pawns[otherId]) == true;

        public bool IsLoveRelation(string def) => LovePartnerRelationUtility.IsLovePartnerRelation(Def(def));

        public bool HasOtherLovePartnerSince(int pawnId, int exceptId, int sinceTick) =>
            pawns[pawnId].relations?.DirectRelations.Any(r => LovePartnerRelationUtility.IsLovePartnerRelation(r.def)
                && r.otherPawn is not null && r.otherPawn.thingIDNumber != exceptId && r.startTicks > sinceTick) == true;

        /// <summary>
        /// Vanilla's add, then the relation's own start back; the fiancé worker re-rolls who takes whose name, so both pawns'
        /// planned name change is put back too (the leaver's came in its blob, the other's never changed).
        /// </summary>
        public void AddRelation(int holderId, string def, int otherId, int startTicks)
        {
            var holder = pawns[holderId];
            var other = pawns[otherId];
            var relation = Def(def)!;
            var holderNameChange = holder.relations.nextMarriageNameChange;
            var otherNameChange = other.relations.nextMarriageNameChange;
            holder.relations.AddDirectRelation(relation, other);
            holder.relations.GetDirectRelation(relation, other).startTicks = startTicks;
            if (relation.reflexive) other.relations.GetDirectRelation(relation, holder).startTicks = startTicks;
            holder.relations.nextMarriageNameChange = holderNameChange;
            other.relations.nextMarriageNameChange = otherNameChange;
        }

        private static PawnRelationDef? Def(string name) => DefDatabase<PawnRelationDef>.GetNamedSilentFail(name);
    }

    /// <summary>A <see cref="RelationEntry"/> as the save keeps it.</summary>
    public sealed class SavedEntry : IExposable
    {
        public string key = "";
        public int thingId;
        public int leftTick;
        public List<string> defs = [];
        public List<int> others = [];
        public List<int> starts = [];
        public List<bool> heldByOther = [];

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key", "");
            Scribe_Values.Look(ref thingId, "thingId");
            Scribe_Values.Look(ref leftTick, "leftTick");
            Scribe_Collections.Look(ref defs, "defs", LookMode.Value);
            Scribe_Collections.Look(ref others, "others", LookMode.Value);
            Scribe_Collections.Look(ref starts, "starts", LookMode.Value);
            Scribe_Collections.Look(ref heldByOther, "heldByOther", LookMode.Value);
        }

        public static SavedEntry From(RelationEntry e) => new()
        {
            key = e.Key,
            thingId = e.ThingId,
            leftTick = e.LeftTick,
            defs = e.Edges.Select(x => x.Def).ToList(),
            others = e.Edges.Select(x => x.OtherId).ToList(),
            starts = e.Edges.Select(x => x.StartTicks).ToList(),
            heldByOther = e.Edges.Select(x => x.HeldByOther).ToList(),
        };

        public RelationEntry ToEntry()
        {
            var entry = new RelationEntry { Key = key ?? "", ThingId = thingId, LeftTick = leftTick };
            int n = new[] { defs?.Count ?? 0, others?.Count ?? 0, starts?.Count ?? 0, heldByOther?.Count ?? 0 }.Min();
            for (int i = 0; i < n; i++) entry.Edges.Add(new RelationEdge(defs![i], others![i], starts![i], heldByOther![i]));
            return entry;
        }
    }
}
