namespace Rimlink.Client.Relations;

/// <summary>
/// One direct relation a pawn left behind. <see cref="HeldByOther"/> false: it sat in the leaver's own list (both halves of a
/// reflexive relation come back from it); true: in the other pawn's list (a child's Parent).
/// </summary>
public sealed record RelationEdge(string Def, int OtherId, int StartTicks, bool HeldByOther);

/// <summary>A pawn of the colony that left for good: its fingerprint, its thing id here, when it left and what it left behind.</summary>
public sealed class RelationEntry
{
    public string Key = "";
    public int ThingId;
    public int LeftTick;
    public List<RelationEdge> Edges = [];
}

/// <summary>The game the book acts on, by thing id (the mod implements it over Verse).</summary>
public interface IRelationWorld
{
    /// <summary>Alive or dead, anywhere in the game.</summary>
    bool PawnExists(int id);
    bool DefExists(string def);
    bool HasRelation(int holderId, string def, int otherId);
    bool IsLoveRelation(string def);
    /// <summary>The pawn has a love relation with someone other than <paramref name="exceptId"/> that started after <paramref name="sinceTick"/>.</summary>
    bool HasOtherLovePartnerSince(int pawnId, int exceptId, int sinceTick);
    void AddRelation(int holderId, string def, int otherId, int startTicks);
}

public enum EdgeOutcome { Restored, Deferred, OtherGone, AlreadyThere, NewPartner, UnknownDef, Failed }

public sealed record EdgeResult(RelationEdge Edge, EdgeOutcome Outcome, string? Error = null);

/// <summary>
/// The relations of the colony's pawns that are away (spec "relations on return" §3–4): recorded when a pawn leaves, given back
/// when it comes home. Edges to a pawn that is itself away wait in that pawn's entry until it is back.
/// </summary>
public sealed class RelationBook
{
    public List<RelationEntry> Entries { get; } = [];

    public static string KeyOf(string raceDef, string gender, long birthAbsTicks, string fullName) => $"{raceDef}|{gender}|{birthAbsTicks}|{fullName}";

    /// <summary>An entry with nothing to give back is not kept; a pawn that left again replaces its old entry.</summary>
    public void Record(RelationEntry entry)
    {
        Entries.RemoveAll(e => e.Key == entry.Key);
        if (entry.Edges.Count > 0) Entries.Add(entry);
    }

    public RelationEntry? Take(string key)
    {
        var entry = Entries.Find(e => e.Key == key);
        if (entry is not null) Entries.Remove(entry);
        return entry;
    }

    public bool IsAway(int thingId) => Entries.Exists(e => e.ThingId == thingId);

    /// <summary>Gives the pawn (now <paramref name="pawnId"/>) the relations of <paramref name="entry"/>, one result per edge.</summary>
    public List<EdgeResult> Apply(RelationEntry entry, int pawnId, IRelationWorld world)
    {
        var results = new List<EdgeResult>();
        foreach (var edge in entry.Edges)
        {
            try
            {
                results.Add(new EdgeResult(edge, ApplyEdge(entry, edge, pawnId, world)));
            }
            catch (Exception ex)
            {
                results.Add(new EdgeResult(edge, EdgeOutcome.Failed, ex.Message));
            }
        }
        return results;
    }

    private EdgeOutcome ApplyEdge(RelationEntry entry, RelationEdge edge, int pawnId, IRelationWorld world)
    {
        if (!world.DefExists(edge.Def)) return EdgeOutcome.UnknownDef;
        if (!world.PawnExists(edge.OtherId))
        {
            var away = Entries.Find(e => e.ThingId == edge.OtherId);
            if (away is null) return EdgeOutcome.OtherGone;
            var handed = edge with { OtherId = pawnId, HeldByOther = !edge.HeldByOther };
            if (!away.Edges.Contains(handed)) away.Edges.Add(handed);
            return EdgeOutcome.Deferred;
        }
        int holder = edge.HeldByOther ? edge.OtherId : pawnId;
        int other = edge.HeldByOther ? pawnId : edge.OtherId;
        if (world.HasRelation(holder, edge.Def, other)) return EdgeOutcome.AlreadyThere;
        if (world.IsLoveRelation(edge.Def) && world.HasOtherLovePartnerSince(edge.OtherId, pawnId, entry.LeftTick)) return EdgeOutcome.NewPartner;
        world.AddRelation(holder, edge.Def, other, edge.StartTicks);
        return EdgeOutcome.Restored;
    }

    /// <summary>Drops the entries none of whose relations can ever come back: every other pawn is gone and none of them is away.</summary>
    public int Prune(Func<int, bool> pawnExists)
    {
        var away = Entries.Select(e => e.ThingId).ToHashSet();
        return Entries.RemoveAll(e => !e.Edges.Exists(edge => pawnExists(edge.OtherId) || away.Contains(edge.OtherId)));
    }
}
