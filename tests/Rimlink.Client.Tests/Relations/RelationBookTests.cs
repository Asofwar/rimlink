using Rimlink.Client.Relations;
using Xunit;

namespace Rimlink.Client.Tests.Relations;

/// <summary>The Verse-free relation book: what a pawn leaves behind and what it gets back when it comes home.</summary>
public class RelationBookTests
{
    private sealed class FakeWorld : IRelationWorld
    {
        public HashSet<int> Pawns = [];
        public HashSet<(int Holder, string Def, int Other)> Relations = [];
        public HashSet<string> LoveDefs = [];
        public HashSet<(int Pawn, int Except)> NewPartners = [];
        public List<(int Holder, string Def, int Other, int Start)> Added = [];

        public bool PawnExists(int id) => Pawns.Contains(id);
        public bool DefExists(string def) => def != "Gone";
        public bool HasRelation(int holderId, string def, int otherId) => Relations.Contains((holderId, def, otherId));
        public bool IsLoveRelation(string def) => LoveDefs.Contains(def);
        public bool HasOtherLovePartnerSince(int pawnId, int exceptId, int sinceTick) => NewPartners.Contains((pawnId, exceptId));

        public void AddRelation(int holderId, string def, int otherId, int startTicks)
        {
            if (def == "Boom") throw new InvalidOperationException("boom");
            Added.Add((holderId, def, otherId, startTicks));
        }
    }

    private static RelationEntry Entry(string key, int thingId, params RelationEdge[] edges) =>
        new() { Key = key, ThingId = thingId, LeftTick = 100, Edges = [.. edges] };

    [Fact]
    public void RecordReplacesAnEntryWithTheSameKey()
    {
        var book = new RelationBook();
        book.Record(Entry("Human|Male|5|A B", 10, new RelationEdge("Spouse", 20, 50, false)));
        book.Record(Entry("Human|Male|5|A B", 11, new RelationEdge("Lover", 30, 60, false)));

        var only = Assert.Single(book.Entries);
        Assert.Equal(11, only.ThingId);

        var empty = new RelationBook();
        empty.Record(Entry("Human|Female|7|C D", 12));
        Assert.Empty(empty.Entries);
    }

    [Fact]
    public void TakeWithAnUnknownKeyReturnsNull()
    {
        var book = new RelationBook();
        book.Record(Entry("k", 10, new RelationEdge("Spouse", 20, 50, false)));

        Assert.Null(book.Take("x"));
        Assert.Single(book.Entries);
        Assert.Equal(10, book.Take("k")!.ThingId);
        Assert.Empty(book.Entries);
    }

    [Fact]
    public void ApplyRestoresEdgesFromBothSides()
    {
        var book = new RelationBook();
        var world = new FakeWorld { Pawns = [20, 30] };
        var entry = Entry("k", 10, new RelationEdge("Spouse", 20, 50, false), new RelationEdge("Parent", 30, 7, true));

        var results = book.Apply(entry, 10, world);

        Assert.Equal([(10, "Spouse", 20, 50), (30, "Parent", 10, 7)], world.Added);
        Assert.All(results, r => Assert.Equal(EdgeOutcome.Restored, r.Outcome));
    }

    [Fact]
    public void ApplySkipsAGoneOtherAndAnExistingRelation()
    {
        var book = new RelationBook();
        var world = new FakeWorld { Pawns = [20], Relations = [(10, "Lover", 20)] };
        var entry = Entry("k", 10, new RelationEdge("Spouse", 40, 50, false), new RelationEdge("Lover", 20, 60, false));

        var results = book.Apply(entry, 10, world);

        Assert.Equal([EdgeOutcome.OtherGone, EdgeOutcome.AlreadyThere], results.Select(r => r.Outcome));
        Assert.Empty(world.Added);
    }

    [Fact]
    public void ApplySkipsALoveRelationWhenTheOtherFoundANewPartner()
    {
        var book = new RelationBook();
        var world = new FakeWorld { Pawns = [20, 30], LoveDefs = ["Spouse"], NewPartners = [(20, 10)] };
        var entry = Entry("k", 10, new RelationEdge("Spouse", 20, 50, false), new RelationEdge("Parent", 30, 7, true));

        var results = book.Apply(entry, 10, world);

        Assert.Equal([EdgeOutcome.NewPartner, EdgeOutcome.Restored], results.Select(r => r.Outcome));
        Assert.Equal([(30, "Parent", 10, 7)], world.Added);
    }

    [Fact]
    public void ApplySkipsUnknownDefs()
    {
        var book = new RelationBook();
        var world = new FakeWorld { Pawns = [20] };
        var entry = Entry("k", 10, new RelationEdge("Gone", 20, 50, false), new RelationEdge("Spouse", 20, 60, false));

        var results = book.Apply(entry, 10, world);

        Assert.Equal([EdgeOutcome.UnknownDef, EdgeOutcome.Restored], results.Select(r => r.Outcome));
        Assert.Equal([(10, "Spouse", 20, 60)], world.Added);
    }

    [Fact]
    public void ApplyGoesOnAfterAFailingEdge()
    {
        var book = new RelationBook();
        var world = new FakeWorld { Pawns = [20] };
        var entry = Entry("k", 10, new RelationEdge("Boom", 20, 50, false), new RelationEdge("Spouse", 20, 60, false));

        var results = book.Apply(entry, 10, world);

        Assert.Equal(EdgeOutcome.Failed, results[0].Outcome);
        Assert.Equal("boom", results[0].Error);
        Assert.Equal(EdgeOutcome.Restored, results[1].Outcome);
        Assert.Equal([(10, "Spouse", 20, 60)], world.Added);
    }

    [Fact]
    public void ApplyDefersAnEdgeToAPawnStillAway()
    {
        var book = new RelationBook();
        book.Record(Entry("A", 10, new RelationEdge("Spouse", 20, 50, false)));
        book.Record(Entry("B", 20, new RelationEdge("Lover", 30, 9, false)));
        var world = new FakeWorld();

        var a = book.Take("A")!;
        var results = book.Apply(a, 10, world);
        book.Apply(a, 10, world);

        Assert.Equal(EdgeOutcome.Deferred, Assert.Single(results).Outcome);
        var b = Assert.Single(book.Entries);
        Assert.Equal([new RelationEdge("Lover", 30, 9, false), new RelationEdge("Spouse", 10, 50, true)], b.Edges);
        Assert.Empty(world.Added);
    }

    [Fact]
    public void DeferredEdgeIsRestoredWhenTheSecondReturns()
    {
        var book = new RelationBook();
        book.Record(Entry("A", 10, new RelationEdge("Spouse", 20, 50, false)));
        book.Record(Entry("B", 20, new RelationEdge("Lover", 30, 9, false)));
        var world = new FakeWorld();
        book.Apply(book.Take("A")!, 10, world);

        world.Pawns = [10];
        var results = book.Apply(book.Take("B")!, 20, world);

        Assert.Contains((10, "Spouse", 20, 50), world.Added);
        Assert.Equal([EdgeOutcome.OtherGone, EdgeOutcome.Restored], results.Select(r => r.Outcome));
    }

    [Fact]
    public void PruneDropsEntriesWhoseOthersAreAllGone()
    {
        var book = new RelationBook();
        book.Record(Entry("gone", 10, new RelationEdge("Spouse", 40, 50, false)));
        book.Record(Entry("away", 11, new RelationEdge("Spouse", 12, 50, false)));
        book.Record(Entry("other", 12, new RelationEdge("Parent", 13, 5, true)));

        int removed = book.Prune(id => id == 13);

        Assert.Equal(1, removed);
        Assert.Equal(["away", "other"], book.Entries.Select(e => e.Key));
    }
}
