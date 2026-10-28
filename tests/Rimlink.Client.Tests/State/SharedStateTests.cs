using Rimlink.Client.State;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.State;

public class SharedStateTests
{
    private static PlayerRef Player(long id, string name) => new() { PlayerId = id, Name = name, FactionName = name };

    private static ServerEvent Added(long id, int tile, long owner) => new()
    {
        WorldObjectAdded = new WorldObjectAdded { Object = new WorldObject { Id = id, Type = "settlement", Tile = tile, Name = $"S{id}", Owner = Player(owner, "P" + owner) } },
    };

    private static ServerEvent Chat(long id, string text) => new()
    {
        Chat = new ChatMessageEvent { Id = id, Text = text, From = Player(1, "P1"), Kind = MessageKind.Player },
    };

    [Fact]
    public void DuplicateParcelArrivedIsStoredOnce()
    {
        var state = new SharedState();
        int raised = 0;
        state.Economy.ParcelArrived += _ => raised++;
        var parcel = new Parcel { Id = 7, Kind = ParcelKind.ParcelGift, Silver = 5 };

        state.Apply(new ServerEvent { ParcelArrived = new ParcelArrived { Parcel = parcel } });
        state.Apply(new ServerEvent { ParcelArrived = new ParcelArrived { Parcel = parcel } });
        state.Apply(new ServerEvent { WalletChanged = new WalletChanged { Balance = 42, Delta = 42, Kind = "admin" } });
        state.Apply(new ServerEvent { LotClosed = new LotClosed { LotId = 3, Reason = "expired" } });

        Assert.Single(state.Economy.Pending);
        Assert.Equal(1, raised);
        Assert.Equal(42, state.Economy.Balance);
        state.Reset();
        Assert.Empty(state.Economy.Pending);
        Assert.Equal(0, state.Economy.Balance);
    }

    [Fact]
    public void ChatMessageSeenInHistoryAndAsLiveEventIsStoredOnce()
    {
        var state = new SharedState();
        var history = new ChatHistory();
        history.Messages.Add(Chat(5, "admin joined the server").Chat);
        state.LoadSnapshot(new WorldObjects(), history, []);

        state.Apply(Chat(5, "admin joined the server")); // the same line arrives on the event stream right after login

        Assert.Single(state.Chat.Messages);
    }

    [Fact]
    public void DuplicateWorldObjectAddedIsIgnored()
    {
        var state = new SharedState();
        int added = 0;
        state.ObjectAdded += _ => added++;

        state.Apply(Added(10, 100, 1));
        state.Apply(Added(10, 100, 1)); // replayed after a reconnect

        Assert.Equal(1, added);
        Assert.Single(state.Objects.All);
        Assert.Equal(10, state.Objects.TryGetByTile(100)?.Id);
    }

    [Fact]
    public void RemovedObjectDisappearsFromTileIndex()
    {
        var state = new SharedState();
        long? removed = null;
        state.ObjectRemoved += id => removed = id;
        state.Apply(Added(10, 100, 1));

        state.Apply(new ServerEvent { WorldObjectRemoved = new WorldObjectRemoved { ObjectId = 10 } });
        state.Apply(new ServerEvent { WorldObjectRemoved = new WorldObjectRemoved { ObjectId = 10 } }); // already gone

        Assert.Equal(10, removed);
        Assert.Null(state.Objects.TryGetByTile(100));
        Assert.Empty(state.Objects.All);
    }

    [Fact]
    public void ChatLogKeepsLastFiveHundred()
    {
        var state = new SharedState();

        for (int i = 1; i <= 520; i++) state.Apply(Chat(i, "m" + i));

        Assert.Equal(500, state.Chat.Messages.Count);
        Assert.Equal("m21", state.Chat.Messages[0].Text);
        Assert.Equal("m520", state.Chat.Messages[^1].Text);
    }

    [Fact]
    public void PlayersOnlineFollowJoinAndLeave()
    {
        var state = new SharedState();

        state.Apply(new ServerEvent { PlayerJoined = new PlayerJoined { Player = Player(2, "Bob") } });
        state.Apply(new ServerEvent { PlayerJoined = new PlayerJoined { Player = Player(2, "Bob") } }); // duplicate
        state.Apply(new ServerEvent { PlayerJoined = new PlayerJoined { Player = Player(3, "Carol") } });
        state.Apply(new ServerEvent { PlayerLeft = new PlayerLeft { Player = Player(2, "Bob") } });

        Assert.Equal(["Carol"], state.Players.Online.Select(p => p.Name));
    }

    [Fact]
    public void SnapshotReplacesEverythingAndResetClears()
    {
        var state = new SharedState();
        state.Apply(Added(1, 1, 1));
        var objects = new WorldObjects();
        objects.Objects.Add(new WorldObject { Id = 7, Tile = 7, Type = "settlement", Owner = Player(1, "P1") });
        var history = new ChatHistory();
        history.Messages.Add(new ChatMessageEvent { Id = 1, Text = "old" });

        state.LoadSnapshot(objects, history, [Player(1, "P1")]);
        Assert.Equal([7L], state.Objects.All.Select(o => o.Id));
        Assert.Equal(["old"], state.Chat.Messages.Select(m => m.Text));
        Assert.Single(state.Players.Online);

        state.Reset();
        Assert.Empty(state.Objects.All);
        Assert.Empty(state.Chat.Messages);
        Assert.Empty(state.Players.Online);
    }

    [Fact]
    public void InboxDrainsInOrderAndStopsAtMax()
    {
        var inbox = new EventInbox();
        for (int i = 1; i <= 5; i++) inbox.Enqueue(Chat(i, "m" + i));
        var seen = new List<string>();

        int drained = inbox.DrainTo(ev => seen.Add(ev.Chat.Text), max: 3);

        Assert.Equal(3, drained);
        Assert.Equal(["m1", "m2", "m3"], seen);
        Assert.Equal(2, inbox.Count);
    }

    [Fact]
    public void NoticesAndRollbacksAreQueuedForTheGameLayer()
    {
        var state = new SharedState();

        state.Apply(new ServerEvent { Notice = new Notice { Text = "hello" } });
        state.Apply(new ServerEvent { SaveRolledBack = new SaveRolledBack { Version = 3 } });
        state.Apply(new ServerEvent { Shutdown = new ServerShutdown { InSeconds = 60, Reason = "patch" } });

        Assert.Equal(["hello"], state.Notices.Select(n => n.Text));
        Assert.Equal(3, state.PendingRollbackVersion);
        Assert.Equal(60, state.PendingShutdown?.InSeconds);
    }
}
