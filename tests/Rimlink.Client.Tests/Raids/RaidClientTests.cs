using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Raids;
using Rimlink.Client.State;
using Rimlink.Client.Tests.Support;
using Rimlink.Client.Visits;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Client.Tests.Raids;

public class RaidClientTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-raid-outbox-" + Guid.NewGuid().ToString("N"));

    private TransferOutbox Outbox() => new(Path.Combine(dir, "outbox.json"), new ConsoleLog());

    private static OutboxClients ClientsOf(RimlinkConnection c) => new(new EconomyClient(c), new RaidClient(c), new ShopClient(c));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task RaidsStateGetsQueuedRaidFromTheEventStream()
    {
        using var fixture = RaidServer();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.Login!.Player.PlayerId);
        var state = new SharedState();
        bob.EventReceived += state.Apply;
        int queued = 0;
        state.Raids.RaidQueued += _ => queued++;
        var nova = await fixture.RegisterAndLogin("nova");
        var n = RaidClientsOf(fixture, nova.SessionToken);

        var raid = await n.Raids.LaunchAsync(Launch(bob.Login.Player.PlayerId), n.Auth);

        await ClientFixtures.WaitUntil(() => queued == 1); // the event thread lists the raid first, then raises the event
        Assert.Equal(raid.Id, Assert.Single(state.Raids.Incoming).Id);
        state.Apply(new ServerEvent { RaidQueued = new RaidQueued { Raid = state.Raids.Incoming[0] } }); // replay
        Assert.Single(state.Raids.Incoming);
        Assert.Equal(1, queued);
    }

    [Fact]
    public async Task LoadFillsIncomingMineAndFlags()
    {
        using var fixture = RaidServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.Login!.Player.PlayerId);
        var raids = new RaidClient(nova);
        await raids.Launch(Launch(bob.Login.Player.PlayerId), CancellationToken.None);
        await new RaidClient(bob).SetPeaceful(true, CancellationToken.None);

        var state = new SharedState();
        state.Raids.Load(await new RaidClient(bob).Pending(CancellationToken.None), await raids.Mine(CancellationToken.None), await new RaidClient(bob).Flags(CancellationToken.None));

        Assert.Single(state.Raids.Incoming);
        Assert.Single(state.Raids.Mine);
        Assert.True(state.Raids.Flags.Peaceful);
        state.Raids.MarkLanded(state.Raids.Incoming[0].Id);
        Assert.Empty(state.Raids.Incoming);
    }

    [Fact]
    public async Task OutboxReplaysALaunchOnce()
    {
        using var fixture = RaidServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        var outbox = Outbox();
        outbox.Add(Launch(bob.Player.PlayerId));

        var first = await outbox.Replay(ClientsOf(nova), CancellationToken.None);
        var second = await Outbox().Replay(ClientsOf(nova), CancellationToken.None);

        Assert.Equal(1, first.Completed);
        Assert.Equal(0, second.Completed);
        var b = RaidClientsOf(fixture, bob.SessionToken);
        Assert.Single((await b.Raids.PendingAsync(new Empty(), b.Auth)).Raids);
    }

    [Fact]
    public async Task OutboxReplaysLandAndReport()
    {
        using var fixture = RaidServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.Login!.Player.PlayerId);
        var raid = await new RaidClient(nova).Launch(Launch(bob.Login.Player.PlayerId, pawns: 1), CancellationToken.None);
        await new RaidClient(bob).Announce(raid.Id, 1000, CancellationToken.None);
        var outbox = Outbox();
        outbox.Add(new RaidTick { RaidId = raid.Id, GameTick = 70000 });
        var report = new RaidReport { RaidId = raid.Id, GameTick = 80000, Loot = new ItemList(), Captives = new PawnList() };
        report.Outcomes.Add(new PawnOutcomeMsg { Index = 0, Outcome = PawnOutcome.OutcomeDead });
        outbox.Add(report);

        var result = await outbox.Replay(ClientsOf(bob), CancellationToken.None);

        Assert.Equal(2, result.Completed);
        Assert.Empty(outbox.Entries);
        Assert.Equal(RaidState.RaidResolved, Assert.Single((await new RaidClient(nova).Mine(CancellationToken.None)).Raids).State);
    }

    [Fact]
    public async Task ARefusedLaunchKeepsItsPawnsForReturn()
    {
        using var fixture = RaidServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        var b = RaidClientsOf(fixture, bob.SessionToken);
        await b.Raids.SetPeacefulAsync(new PeacefulRequest { Peaceful = true }, b.Auth);
        var outbox = Outbox();
        var entry = outbox.Add(Launch(bob.Player.PlayerId, pawns: 2));

        var result = await outbox.Replay(ClientsOf(nova), CancellationToken.None);

        Assert.Equal(1, result.Refused);
        var kept = Assert.Single(Outbox().Entries);
        Assert.Equal(entry.Id, kept.Id);
        Assert.NotNull(kept.Refusal);
        Assert.Equal(2, Assert.IsType<LaunchRequest>(TransferOutbox.Decode(kept)).Pawns.Pawns.Count);
    }

    [Fact]
    public async Task ARefusedReportKeepsItsLootForReturn()
    {
        using var fixture = RaidServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.Login!.Player.PlayerId);
        var raid = await new RaidClient(nova).Launch(Launch(bob.Login.Player.PlayerId, pawns: 1), CancellationToken.None);
        var outbox = Outbox();
        var report = new RaidReport { RaidId = raid.Id, GameTick = 80000, Loot = new ItemList(), Captives = new PawnList() };
        report.Outcomes.Add(new PawnOutcomeMsg { Index = 0, Outcome = PawnOutcome.OutcomeDead });
        report.Loot.Items.Add(Server.Tests.Support.EconomyFixtures.Item("Steel", 5, marketValueX100: 190));
        var entry = outbox.Add(report); // the raid was never announced: the server refuses the report for good

        var result = await outbox.Replay(ClientsOf(bob), CancellationToken.None);

        Assert.Equal(1, result.Refused);
        var kept = Assert.Single(Outbox().Entries);
        Assert.Equal(entry.Id, kept.Id);
        Assert.NotNull(kept.Refusal);
        Assert.Equal(5, Assert.Single(Assert.IsType<RaidReport>(TransferOutbox.Decode(kept)).Loot.Items).Count);
    }

    [Fact]
    public async Task ARefusedReportWithoutLootIsDropped()
    {
        using var fixture = RaidServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.Login!.Player.PlayerId);
        var raid = await new RaidClient(nova).Launch(Launch(bob.Login.Player.PlayerId, pawns: 1), CancellationToken.None);
        var outbox = Outbox();
        var report = new RaidReport { RaidId = raid.Id, GameTick = 80000, Loot = new ItemList(), Captives = new PawnList() };
        report.Outcomes.Add(new PawnOutcomeMsg { Index = 0, Outcome = PawnOutcome.OutcomeDead });
        outbox.Add(report); // never announced: refused for good, and no loot to bring back

        var result = await outbox.Replay(ClientsOf(bob), CancellationToken.None);

        Assert.Equal(1, result.Refused);
        Assert.Empty(Outbox().Entries);
    }

    [Fact]
    public async Task ARefusedSnapshotLaunchIsDropped()
    {
        using var fixture = RaidServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        var b = RaidClientsOf(fixture, bob.SessionToken);
        await b.Raids.SetPeacefulAsync(new PeacefulRequest { Peaceful = true }, b.Auth);
        var outbox = Outbox();
        outbox.Add(Launch(bob.Player.PlayerId, pawns: 2, mode: PawnMode.PawnSnapshot)); // copies: the originals never left

        var result = await outbox.Replay(ClientsOf(nova), CancellationToken.None);

        Assert.Equal(1, result.Refused);
        Assert.Empty(Outbox().Entries);
    }

    [Fact]
    public void RefuseKeepsOnlyEntriesThatHoldSomething()
    {
        var outbox = Outbox();
        var empty = outbox.Add(new RaidReport { RaidId = 1, Loot = new ItemList() });
        var withLoot = new RaidReport { RaidId = 2, Loot = new ItemList() };
        withLoot.Loot.Items.Add(Server.Tests.Support.EconomyFixtures.Item("Steel", 5, marketValueX100: 190));
        var full = outbox.Add(withLoot);

        outbox.Refuse(empty.Id, "raid gone");
        outbox.Refuse(full.Id, "raid gone");

        var kept = Assert.Single(Outbox().Entries);
        Assert.Equal(full.Id, kept.Id);
        Assert.Equal("raid gone", kept.Refusal);
    }

    [Fact]
    public async Task ShopClientPublishesAndReads()
    {
        using var fixture = RaidServer();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        var shop = new ShopClient(bob);
        var list = new ShopList();
        list.Items.Add(new ShopItem { ThingId = 7, DefName = "Steel", Count = 3, Quality = -1, Price = 10, SaveTick = 1, Label = "Steel" });

        await shop.Publish(list, CancellationToken.None);

        Assert.Equal(7, Assert.Single((await shop.Of(bob.Login!.Player.PlayerId, CancellationToken.None)).Items).ThingId);
    }
}
