using Rimlink.Client.Game.Live;
using Verse;
using Xunit;

namespace Rimlink.Client.Tests.Colonies;

public sealed class ColonySaveCompatibilityTests : IDisposable
{
    public ColonySaveCompatibilityTests()
    {
        Scribe_Collections.Loading = false;
        Scribe_Collections.Fields.Clear();
    }

    [Fact]
    public void CoopSaveCannotSupplyOldPendingEndOrOrphanCleanupWithHostPawnIds()
    {
        var pawn = new Pawn { thingIDNumber = 42 };
        var owners = new MapComponent_LiveOwners(new Map());
        owners.Loan(pawn, 7);
        owners.ExposeData();

        // 0.6.2 reads only these two dictionaries, both when resolving pending ends and removing orphans.
        var legacyOwners = (Dictionary<int, long>)Scribe_Collections.Fields["rimlinkLiveOwners"];
        var legacyIndexes = (Dictionary<int, int>)Scribe_Collections.Fields["rimlinkLiveIndexes"];
        Assert.Empty(legacyOwners);
        Assert.Empty(legacyIndexes);

        var restored = Load();
        Assert.Equal(7, restored.OwnerOf(pawn));
        Assert.True(restored.IsLoaned(pawn));
        Assert.Equal(new int?[] { 42 }, restored.IdsOf(7));
        restored.ForgetAll(7);
        Assert.False(restored.Any);
        Assert.False(restored.IsOwned(pawn));
        Assert.False(restored.IsLoaned(pawn));
    }

    [Fact]
    public void GuestEscrowKeepsItsIndexesAndCleanupDoesNotTouchLoans()
    {
        var owners = new MapComponent_LiveOwners(new Map());
        var pawn = new Pawn { thingIDNumber = 42 };
        owners.Loan(pawn, 7);
        owners.Mark(new Pawn { thingIDNumber = 51 }, 8, 1);
        owners.Mark(new Pawn { thingIDNumber = 52 }, 8, 3);
        owners.ExposeData();

        var legacyOwners = (Dictionary<int, long>)Scribe_Collections.Fields["rimlinkLiveOwners"];
        Assert.Equal(new[] { 51, 52 }, legacyOwners.Keys.Order());
        Assert.All(legacyOwners.Values, owner => Assert.Equal(8, owner));

        var restored = Load();
        Assert.Equal(new int?[] { null, 51, null, 52 }, restored.IdsOf(8));
        restored.ForgetAll(8);
        Assert.Equal(7, Assert.Single(restored.Owners));
        Assert.Equal(7, restored.OwnerOf(pawn));
        Assert.True(restored.IsOwned(pawn));
    }

    [Fact]
    public void PreCoopSaveStillLoadsGuestEscrow()
    {
        Scribe_Collections.Fields["rimlinkLiveOwners"] = new Dictionary<int, long> { [51] = 8 };
        Scribe_Collections.Fields["rimlinkLiveIndexes"] = new Dictionary<int, int> { [51] = 1 };
        var restored = Load();
        Assert.Equal(new int?[] { null, 51 }, restored.IdsOf(8));
        Assert.False(restored.IsLoaned(new Pawn { thingIDNumber = 51 }));
    }

    private static MapComponent_LiveOwners Load()
    {
        Scribe_Collections.Loading = true;
        var restored = new MapComponent_LiveOwners(new Map());
        restored.ExposeData();
        Scribe_Collections.Loading = false;
        return restored;
    }

    public void Dispose() => Scribe_Collections.Fields.Clear();
}
