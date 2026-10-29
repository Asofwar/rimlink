using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.LiveFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Feed;

/// <summary>Every source of the admin feed (Discord bot spec §2) publishes its event, and nothing private leaks.</summary>
public class FeedSourcesTests
{
    private static ServerFixture WithApi(ServerFixture fixture) => fixture.WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);

    private static string Name(JsonElement data, string player) => data.GetProperty(player).GetProperty("name").GetString()!;

    private static async Task<List<FeedEvent>> DrainAndCheck(FeedProbe probe, int quietMs = 500)
    {
        var events = await probe.Drain(TimeSpan.FromMilliseconds(quietMs));
        Assert.All(events, e => Assert.Contains(e.Type, FeedTypes.All));
        return events;
    }

    [Fact]
    public async Task JoinAndLeaveArePublished()
    {
        using var fixture = WithApi(EconomyServer());
        using var probe = fixture.Probe();

        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var joined = FeedProbe.Data(await probe.Next(FeedTypes.PlayerJoined));
        Assert.Equal("Nova", Name(joined, "player"));
        Assert.Equal(nova.Player.PlayerId, joined.GetProperty("player").GetProperty("id").GetInt64());

        (await fixture.Rest().PostAsJsonAsync($"/api/admin/players/{nova.Player.PlayerId}/kick", new { reason = "test" })).EnsureSuccessStatusCode();
        Assert.Equal("Nova", Name(FeedProbe.Data(await probe.Next(FeedTypes.PlayerLeft)), "player"));
    }

    [Fact]
    public async Task SettleAndAbandonArePublished()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var world = new Protocol.V1.World.WorldClient(fixture.CreateChannel());
        using var probe = fixture.Probe();

        var settlement = await world.SettleAsync(new SettleRequest { Tile = 100, Name = "Nova Home" }, Bearer(nova.SessionToken));
        var founded = FeedProbe.Data(await probe.Next(FeedTypes.ColonyFounded));
        Assert.Equal("Nova", Name(founded, "player"));
        Assert.Equal("Nova Home", founded.GetProperty("colony").GetString());
        Assert.Equal(100, founded.GetProperty("tile").GetInt32());

        await world.AbandonAsync(new AbandonRequest { ObjectId = settlement.Id }, Bearer(nova.SessionToken));
        var removed = FeedProbe.Data(await probe.Next(FeedTypes.ColonyRemoved));
        Assert.Equal("Nova Home", removed.GetProperty("colony").GetString());
    }

    [Fact]
    public async Task BroadcastShutdownAndFreezesArePublished()
    {
        using var fixture = WithApi(EconomyServer());
        var api = fixture.Rest();
        using var probe = fixture.Probe();

        (await api.PostAsJsonAsync("/api/admin/broadcast", new { text = "hello all" })).EnsureSuccessStatusCode();
        Assert.Equal("hello all", FeedProbe.Data(await probe.Next(FeedTypes.Notice)).GetProperty("text").GetString());

        (await api.PostAsJsonAsync("/api/admin/economy/freeze", new { frozen = true, reason = "audit" })).EnsureSuccessStatusCode();
        var economy = FeedProbe.Data(await probe.Next(FeedTypes.EconomyFrozen));
        Assert.True(economy.GetProperty("frozen").GetBoolean());
        Assert.Equal("audit", economy.GetProperty("reason").GetString());

        (await api.PostAsJsonAsync("/api/admin/raids/freeze", new { frozen = false })).EnsureSuccessStatusCode();
        Assert.False(FeedProbe.Data(await probe.Next(FeedTypes.RaidsFrozen)).GetProperty("frozen").GetBoolean());

        (await api.PostAsJsonAsync("/api/admin/shutdown", new { inSeconds = 3600, reason = "maintenance" })).EnsureSuccessStatusCode();
        var shutdown = FeedProbe.Data(await probe.Next(FeedTypes.ServerShutdown));
        Assert.Equal(3600, shutdown.GetProperty("inSeconds").GetInt32());
        Assert.Equal("maintenance", shutdown.GetProperty("reason").GetString());
    }

    private sealed record Raiders(ServerFixture Fixture, LoginReply Nova, LoginReply Bob, RaidClients N, RaidClients B);

    private static async Task<Raiders> NovaAndBob()
    {
        var fixture = RaidServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        return new Raiders(fixture, nova, bob, RaidClientsOf(fixture, nova.SessionToken), RaidClientsOf(fixture, bob.SessionToken));
    }

    [Fact]
    public async Task RaidLandedAndResolvedArePublished()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        using var probe = fixture.Probe();
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, pawns: 3), p.N.Auth);
        await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 1000 }, p.B.Auth);

        await p.B.Raids.LandAsync(new RaidTick { RaidId = raid.Id, GameTick = 70000 }, p.B.Auth);
        var landed = FeedProbe.Data(await probe.Next(FeedTypes.RaidLanded));
        Assert.Equal(raid.Id, landed.GetProperty("raidId").GetInt64());
        Assert.Equal("Nova", Name(landed, "attacker"));
        Assert.Equal("Bob", Name(landed, "target"));

        var report = new RaidReport { RaidId = raid.Id, GameTick = 80000, Loot = new ItemList(), Captives = new PawnList() };
        report.Outcomes.Add(new PawnOutcomeMsg { Index = 0, Outcome = PawnOutcome.OutcomeDead });
        report.Outcomes.Add(new PawnOutcomeMsg { Index = 1, Outcome = PawnOutcome.OutcomeCaptured });
        report.Outcomes.Add(new PawnOutcomeMsg { Index = 2, Outcome = PawnOutcome.OutcomeFled, Blob = Pawn("Colonist", "Runner") });
        await p.B.Raids.ReportAsync(report, p.B.Auth);
        var resolved = FeedProbe.Data(await probe.Next(FeedTypes.RaidResolved));
        Assert.Equal("Nova", Name(resolved, "attacker"));
        Assert.Equal("Bob", Name(resolved, "target"));
        Assert.Equal(1, resolved.GetProperty("dead").GetInt32());
        Assert.Equal(1, resolved.GetProperty("captured").GetInt32());
        Assert.Equal(1, resolved.GetProperty("fled").GetInt32());
    }

    [Fact]
    public async Task RaidQueuedIsNotPublished()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        await Task.Delay(300); // presence events of the logins
        using var probe = fixture.Probe();

        await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, pawns: 2), p.N.Auth);

        Assert.DoesNotContain(await DrainAndCheck(probe), e => e.Type.StartsWith("raid.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VisitIsPublished()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var b = RaidClientsOf(fixture, bob.SessionToken);
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Bob", tick: 10);
        await Upload(b.Saves, bob.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Bob", tick: 10));
        using var probe = fixture.Probe();

        await RaidClientsOf(fixture, nova.SessionToken).Visits.BeginAsync(new VisitRequest { TargetPlayerId = bob.Player.PlayerId, GameTick = 5 }, Bearer(nova.SessionToken));

        var visit = FeedProbe.Data(await probe.Next(FeedTypes.VisitStarted));
        Assert.Equal("Nova", Name(visit, "visitor"));
        Assert.Equal("Bob", Name(visit, "host"));
    }

    private static async Task<(ServerFixture Fixture, LiveClients Nova, LiveClients Bob)> LivePair(bool autoAccept)
    {
        var fixture = WithApi(LiveServer());
        var nova = await Connected(fixture, "nova");
        var bob = await Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.PlayerId);
        if (autoAccept) await bob.Live.SetAutoAcceptAsync(new AutoAcceptRequest { Enabled = true }, bob.Auth);
        return (fixture, nova, bob);
    }

    [Fact]
    public async Task LiveStartAndEndArePublishedOnce()
    {
        var (fixture, nova, bob) = await LivePair(autoAccept: true);
        using var _ = fixture;
        using var probe = fixture.Probe();

        var session = await nova.Live.ProposeAsync(Propose(bob.PlayerId, LiveKind.LiveRaid), nova.Auth);
        var started = FeedProbe.Data(await probe.Next(FeedTypes.LiveStarted));
        Assert.Equal(session.Id, started.GetProperty("sessionId").GetInt64());
        Assert.Equal("raid", started.GetProperty("kind").GetString());
        Assert.Equal("Bob", Name(started, "host"));
        Assert.Equal("Nova", Name(started, "guest"));

        using (var scope = fixture.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<LiveSessionService>().MarkRunning(session.Id, bob.PlayerId);
        await bob.Raids.ReportAsync(AllAlive(session.RaidId, session.Id, pawns: 2), bob.Auth);

        var ended = (await DrainAndCheck(probe)).Where(e => e.Type == FeedTypes.LiveEnded).ToList();
        Assert.Equal("normal", FeedProbe.Data(Assert.Single(ended)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AdminEndOfALiveSessionIsPublished()
    {
        var (fixture, nova, bob) = await LivePair(autoAccept: true);
        using var _ = fixture;
        var session = await nova.Live.ProposeAsync(Propose(bob.PlayerId, LiveKind.LiveVisit), nova.Auth);
        using var probe = fixture.Probe();

        (await fixture.Rest().PostAsJsonAsync($"/api/admin/live/{session.Id}/end", new { reason = "test" })).EnsureSuccessStatusCode();

        var ended = (await DrainAndCheck(probe)).Where(e => e.Type == FeedTypes.LiveEnded).ToList();
        var data = FeedProbe.Data(Assert.Single(ended));
        Assert.Equal("admin", data.GetProperty("reason").GetString());
        Assert.Equal("visit", data.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task DeclinedProposalPublishesNothingLive()
    {
        var (fixture, nova, bob) = await LivePair(autoAccept: false);
        using var _ = fixture;
        using var probe = fixture.Probe();

        var session = await nova.Live.ProposeAsync(Propose(bob.PlayerId, LiveKind.LiveRaid), nova.Auth);
        await bob.Live.RespondAsync(new LiveAnswer { SessionId = session.Id, Accept = false }, bob.Auth);

        Assert.DoesNotContain(await DrainAndCheck(probe), e => e.Type.StartsWith("live.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LotPostedAndSoldArePublished()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await Fund(fixture, bob.Player.PlayerId, 1000);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        using var probe = fixture.Probe();

        var lot = await n.Market.PostAsync(new PostRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Items = Items(("Steel", 50)), Price = 300, GameTick = 100 }, n.Auth);
        var posted = FeedProbe.Data(await probe.Next(FeedTypes.LotPosted));
        Assert.Equal(lot.Id, posted.GetProperty("lotId").GetInt64());
        Assert.Equal("Nova", Name(posted, "seller"));
        Assert.Equal(JsonValueKind.Null, posted.GetProperty("buyer").ValueKind);
        Assert.Equal(300, posted.GetProperty("price").GetInt64());
        Assert.Equal("Steel ×50", posted.GetProperty("summary").GetString());

        await b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth);
        var sold = FeedProbe.Data(await probe.Next(FeedTypes.LotSold));
        Assert.Equal("Nova", Name(sold, "seller"));
        Assert.Equal("Bob", Name(sold, "buyer"));
        Assert.Equal("Steel ×50", sold.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ShopSaleIsPublished()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = RaidClientsOf(fixture, nova.SessionToken);
        var b = RaidClientsOf(fixture, bob.SessionToken);
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Bob", tick: 10);
        await Upload(b.Saves, bob.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Bob", tick: 10));
        await b.Shop.PublishAsync(new ShopList { Items = { new ShopItem { ThingId = 501, DefName = "Steel", Count = 3, Quality = -1, Price = 100, SaveTick = 10, Label = "steel" } } }, b.Auth);
        await Fund(fixture, nova.Player.PlayerId, 1000);
        await n.Visits.BeginAsync(new VisitRequest { TargetPlayerId = bob.Player.PlayerId, GameTick = 5 }, n.Auth);
        using var probe = fixture.Probe();

        await n.Shop.BuyAsync(new BuyRequest { ClientRequestId = Guid.NewGuid().ToString("N"), HostPlayerId = bob.Player.PlayerId, ThingId = 501, Count = 2, Blob = Item("Steel", 2) }, n.Auth);

        var sale = FeedProbe.Data(await probe.Next(FeedTypes.ShopSold));
        Assert.Equal("Bob", Name(sale, "host"));
        Assert.Equal("Nova", Name(sale, "buyer"));
        Assert.Equal("steel", sale.GetProperty("item").GetString());
        Assert.Equal(2, sale.GetProperty("count").GetInt32());
        Assert.Equal(200, sale.GetProperty("price").GetInt64());
    }

    [Fact]
    public async Task AdminActionCarriesTheActorName()
    {
        using var fixture = WithApi(EconomyServer()).WithSetting("Auth:AdminLogins:0", "admin");
        var admin = await fixture.RegisterAndLogin("admin", playerName: "Admin");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        using var probe = fixture.Probe();

        (await fixture.Rest(actor: "discord:Mod (1)").PostAsJsonAsync("/api/admin/broadcast", new { text = "one" })).EnsureSuccessStatusCode();
        var labelled = FeedProbe.Data(await probe.Next(FeedTypes.AdminAction));
        Assert.Equal("broadcast", labelled.GetProperty("action").GetString());
        Assert.Equal("api:discord:Mod (1)", labelled.GetProperty("actor").GetString());
        Assert.Equal("one", labelled.GetProperty("details").GetString());

        (await fixture.Rest().PostAsJsonAsync("/api/admin/broadcast", new { text = "two" })).EnsureSuccessStatusCode();
        Assert.Equal("api", FeedProbe.Data(await probe.Next(FeedTypes.AdminAction)).GetProperty("actor").GetString());

        await new Protocol.V1.Chat.ChatClient(fixture.CreateChannel()).SendAsync(new SendRequest { Text = "/kick Bob spam" }, Bearer(admin.SessionToken));
        var kick = FeedProbe.Data(await probe.Next(FeedTypes.AdminAction));
        Assert.Equal("kick", kick.GetProperty("action").GetString());
        Assert.Equal("Admin", kick.GetProperty("actor").GetString());
        Assert.Equal("Bob", Name(kick, "target"));
    }

    [Fact]
    public async Task PrivateEventsAreNotPublished()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await Fund(fixture, nova.Player.PlayerId, 1000);
        await Task.Delay(300); // presence events of the logins
        using var probe = fixture.Probe();

        var n = Clients(fixture, nova.SessionToken);
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 5)), silver: 100), n.Auth);

        Assert.Empty((await DrainAndCheck(probe)).Where(e => e.Type != FeedTypes.ChatMessage));
    }
}
