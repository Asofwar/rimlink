using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Client.Economy;
using Rimlink.Client.Live;
using Rimlink.Client.Net;
using Rimlink.Client.Raids;
using Rimlink.Client.State;
using Rimlink.Client.Tests.Support;
using Rimlink.Client.Visits;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.LiveFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using LiveSession = Rimlink.Protocol.V1.LiveSession;

namespace Rimlink.Client.Tests.Live;

/// <summary>The live client against a real server: the stream, heartbeats, orders, reconnection and the outbox entry.</summary>
public class LiveClientTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-live-outbox-" + Guid.NewGuid().ToString("N"));

    private TransferOutbox Outbox() => new(Path.Combine(dir, "outbox.json"), new ConsoleLog());

    private static OutboxClients ClientsOf(RimlinkConnection c) => new(new EconomyClient(c), new RaidClient(c), new ShopClient(c), new LiveClient(c, new ConsoleLog()));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private sealed record Setup(ServerFixture Fixture, RimlinkConnection Nova, LiveClients Bob, LiveSession Session);

    /// <summary>Nova (guest, client-core connection) and Bob (host, raw test stream) with a Starting raid session.</summary>
    private static async Task<Setup> Started(ServerFixture? fixture = null)
    {
        fixture ??= LiveServer();
        var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.PlayerId);
        await bob.Live.SetAutoAcceptAsync(new AutoAcceptRequest { Enabled = true }, bob.Auth);
        var session = await new LiveClient(nova, new ConsoleLog()).Propose(Propose(bob.PlayerId, LiveKind.LiveRaid), CancellationToken.None);
        return new Setup(fixture, nova, bob, session);
    }

    /// <summary>The host's raw stream, attached: a guest Hello that reached the server first would never come to the host as PeerHello.</summary>
    private static async Task<AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg>> OpenHost(ServerFixture fixture, LiveClients c, long sessionId)
    {
        var call = c.Live.Stream(c.Auth);
        await call.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, Hello = new Hello { Role = LiveRelay.HostRole } });
        var relay = fixture.Services.GetRequiredService<LiveRelay>();
        await ClientFixtures.WaitUntil(() => relay.IsAttached(sessionId, c.PlayerId));
        return call;
    }

    private static async Task<LiveServerMsg> Read(AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> call, LiveServerMsg.MOneofCase kind, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (await call.ResponseStream.MoveNext(cts.Token))
        {
            if (call.ResponseStream.Current.MCase == kind) return call.ResponseStream.Current;
        }
        throw new TimeoutException($"no {kind} frame");
    }

    /// <summary>The next queued frame of that kind (the host's own Hello may arrive first when the streams race).</summary>
    private static async Task<LiveServerMsg> Next(LiveClient guest, LiveServerMsg.MOneofCase kind)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (guest.Incoming.TryDequeue(out var frame)) { if (frame!.MCase == kind) return frame; continue; }
            await Task.Delay(25);
        }
        throw new TimeoutException($"no {kind} frame");
    }

    private static Task KeepAlive(AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> host, long sessionId, CancellationToken ct) => Task.Run(async () =>
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(400, ct); await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, Heartbeat = new Empty() }); } }
        catch { }
    });

    private static async Task<LiveStateDb> StateOf(ServerFixture fixture, long id)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().LiveSessions.AsNoTracking().SingleAsync(s => s.Id == id)).State;
    }

    [Fact]
    public async Task GuestHelloCarriesTheLetterPause()
    {
        var s = await Started();
        using var fixture = s.Fixture;
        await using var nova = s.Nova;
        using var host = await OpenHost(s.Fixture, s.Bob, s.Session.Id);
        using var alive = new CancellationTokenSource();
        var keepAlive = KeepAlive(host, s.Session.Id, alive.Token);
        var guest = new LiveClient(nova, new ConsoleLog()) { HelloLetterPause = LetterPause.Any };

        await guest.Open(s.Session.Id, LiveRelay.GuestRole, 1, CancellationToken.None);

        Assert.Equal(LetterPause.Any, (await Read(host, LiveServerMsg.MOneofCase.PeerHello)).PeerHello.LetterPause);
        await guest.Close();
        alive.Cancel();
    }

    [Fact]
    public async Task OpenSendsHelloAndHeartbeats()
    {
        var s = await Started(LiveServer().WithSetting("Live:HeartbeatTimeoutSeconds", "3"));
        using var fixture = s.Fixture;
        await using var nova = s.Nova;
        using var host = await OpenHost(s.Fixture, s.Bob, s.Session.Id);
        var guest = new LiveClient(nova, new ConsoleLog());
        using var alive = new CancellationTokenSource();
        var keepAlive = KeepAlive(host, s.Session.Id, alive.Token);

        await guest.Open(s.Session.Id, LiveRelay.GuestRole, 2, CancellationToken.None);

        var hello = await Read(host, LiveServerMsg.MOneofCase.PeerHello);
        Assert.Equal(LiveRelay.GuestRole, hello.PeerHello.Role);
        Assert.Equal(2, hello.PeerHello.SnapshotSeq);
        await Task.Delay(6000); // twice the heartbeat timeout: only the 2 s heartbeats keep the guest attached
        Assert.Equal(LiveStateDb.Running, await StateOf(fixture, s.Session.Id));
        Assert.True(guest.IsOpen);
        await guest.Close();
        alive.Cancel();
    }

    [Fact]
    public async Task OrdersReachTheHostAndResultsComeBack()
    {
        var s = await Started();
        using var fixture = s.Fixture;
        await using var nova = s.Nova;
        using var host = await OpenHost(s.Fixture, s.Bob, s.Session.Id);
        var guest = new LiveClient(nova, new ConsoleLog());
        await guest.Open(s.Session.Id, LiveRelay.GuestRole, 1, CancellationToken.None);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);

        guest.Send(new LiveClientMsg { Order = new Order { Seq = 5, PawnId = 11, MoveTo = new Cell { X = 1, Z = 2 } } });

        var order = await Read(host, LiveServerMsg.MOneofCase.Order);
        Assert.Equal(5, order.Order.Seq);
        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = s.Session.Id, OrderResult = new OrderResult { Seq = 5, Ok = false, Reason = "dead" } });
        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = s.Session.Id, Delta = new Delta { Tick = 60 } });
        Assert.Equal("dead", (await Next(guest, LiveServerMsg.MOneofCase.OrderResult)).OrderResult.Reason);
        Assert.Equal(60, (await Next(guest, LiveServerMsg.MOneofCase.Delta)).Delta.Tick);
        await guest.Close();
    }

    [Fact]
    public async Task ReconnectAfterADropResumesTheSession()
    {
        var s = await Started(LiveServer().WithSetting("Live:HeartbeatTimeoutSeconds", "3"));
        using var fixture = s.Fixture;
        await using var nova = s.Nova;
        using var host = await OpenHost(s.Fixture, s.Bob, s.Session.Id);
        var guest = new LiveClient(nova, new ConsoleLog());
        int reconnected = 0;
        guest.Reconnected += () => reconnected++;
        using var alive = new CancellationTokenSource();
        var keepAlive = KeepAlive(host, s.Session.Id, alive.Token);
        await guest.Open(s.Session.Id, LiveRelay.GuestRole, 1, CancellationToken.None);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);

        guest.DropTransportForTest();

        Assert.Equal(LiveServerMsg.MOneofCase.PeerLost, (await Read(host, LiveServerMsg.MOneofCase.PeerLost, TimeSpan.FromSeconds(8))).MCase);
        Assert.Equal(LiveServerMsg.MOneofCase.PeerBack, (await Read(host, LiveServerMsg.MOneofCase.PeerBack, TimeSpan.FromSeconds(8))).MCase);
        await ClientFixtures.WaitUntil(() => reconnected == 1);
        for (int i = 0; i < 40 && await StateOf(fixture, s.Session.Id) != LiveStateDb.Running; i++) await Task.Delay(50); // the Hello follows the reattach
        Assert.Equal(LiveStateDb.Running, await StateOf(fixture, s.Session.Id));
        Assert.True(guest.IsOpen);
        await guest.Close();
        alive.Cancel();
    }

    [Fact]
    public async Task EndFromThePeerRaisesEndedAndStops()
    {
        var s = await Started();
        using var fixture = s.Fixture;
        await using var nova = s.Nova;
        using var host = await OpenHost(s.Fixture, s.Bob, s.Session.Id);
        var guest = new LiveClient(nova, new ConsoleLog());
        LiveEndReason? ended = null;
        guest.Ended += r => ended = r;
        await guest.Open(s.Session.Id, LiveRelay.GuestRole, 1, CancellationToken.None);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);

        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = s.Session.Id, End = new EndMsg { Reason = LiveEndReason.EndHostAborted } });

        await ClientFixtures.WaitUntil(() => ended is not null);
        Assert.Equal(LiveEndReason.EndHostAborted, ended);
        Assert.False(guest.IsOpen);
        Assert.Equal(LiveEndReason.EndHostAborted, (await Next(guest, LiveServerMsg.MOneofCase.End)).End.Reason); // the host's Hello may be queued first
    }

    [Fact]
    public async Task ProposeEntryReplaysOnce()
    {
        using var fixture = LiveServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.PlayerId);
        var outbox = Outbox();
        var entry = outbox.Add(Propose(bob.PlayerId, LiveKind.LiveRaid));

        var first = await outbox.Replay(ClientsOf(nova), CancellationToken.None);
        var second = await Outbox().Replay(ClientsOf(nova), CancellationToken.None);

        Assert.Equal(1, first.Completed);
        Assert.Equal(0, second.Completed + second.Refused + second.Pending);
        Assert.Empty(Outbox().Entries);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().LiveSessions.CountAsync());
        Assert.Equal(OutboxKind.Propose, entry.Kind);
    }

    [Fact]
    public async Task ARefusedProposeKeepsItsPawnsForReturn()
    {
        using var fixture = LiveServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await fixture.RegisterAndLogin("bob"); // logged in but never at home
        var outbox = Outbox();
        outbox.Add(Propose(bob.Player.PlayerId, LiveKind.LiveVisit, pawns: 2));

        var result = await outbox.Replay(ClientsOf(nova), CancellationToken.None);

        Assert.Equal(1, result.Refused);
        var kept = Assert.Single(Outbox().Entries);
        Assert.NotNull(kept.Refusal);
        Assert.Equal(2, Assert.IsType<LiveProposal>(TransferOutbox.Decode(kept)).Pawns.Pawns.Count);
    }

    [Fact]
    public async Task LiveStateFollowsTheEvents()
    {
        using var fixture = LiveServer();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        await bob.Auth.SetAtHomeAsync(new AtHomeRequest { AtHome = true });
        await MakeTargetRaidable(fixture, bob.Login!.Player.PlayerId);
        var state = new SharedState();
        bob.EventReceived += state.Apply;
        int invited = 0, started = 0;
        state.Live.Invited += _ => invited++;
        state.Live.Started += _ => started++;
        var nova = await Connected(fixture, "nova");

        var session = await nova.Live.ProposeAsync(Propose(bob.Login.Player.PlayerId, LiveKind.LiveVisit), nova.Auth);
        await ClientFixtures.WaitUntil(() => invited == 1);
        Assert.Equal(session.Id, state.Live.PendingInvite!.Session.Id);
        await bob.LiveApi.RespondAsync(new LiveAnswer { SessionId = session.Id, Accept = true });

        await ClientFixtures.WaitUntil(() => started == 1);
        Assert.Null(state.Live.PendingInvite);
        Assert.Equal(Protocol.V1.LiveState.LiveStarting, state.Live.Current!.State);
    }

    /// <summary>A guest stream on a started session, with the host's raw stream past the guest's Hello.</summary>
    private static async Task<(Setup S, AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> Host, LiveClient Guest)> GuestStreaming(ServerFixture? fixture = null)
    {
        var s = await Started(fixture);
        var host = await OpenHost(s.Fixture, s.Bob, s.Session.Id);
        var guest = new LiveClient(s.Nova, new ConsoleLog());
        await guest.Open(s.Session.Id, LiveRelay.GuestRole, 1, CancellationToken.None);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);
        return (s, host, guest);
    }

    [Fact]
    public async Task AGapInTheDeltaNumbersAsksTheHostForAResync()
    {
        var (s, host, _) = await GuestStreaming();
        using var fixture = s.Fixture;
        await using var nova = s.Nova;
        using var _host = host;

        foreach (long seq in new long[] { 1, 2, 4 })
            await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = s.Session.Id, Delta = new Delta { Tick = seq, DeltaSeq = seq } });

        await Read(host, LiveServerMsg.MOneofCase.ResyncRequest);
    }

    [Fact]
    public async Task DeltasTheServerDropsAreNoticedByTheGuest()
    {
        // The server passes Live:DeltasPerSecond deltas per stream and drops the rest silently: at one a second, a burst of ten loses most.
        var (s, host, guest) = await GuestStreaming(LiveServer().WithSetting("Live:DeltasPerSecond", "1"));
        using var fixture = s.Fixture;
        await using var nova = s.Nova;
        using var _host = host;

        for (long seq = 1; seq <= 10; seq++)
            await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = s.Session.Id, Delta = new Delta { Tick = seq, DeltaSeq = seq } });
        await Task.Delay(1500); // the bucket refills; the next delta passes and shows the guest what it missed
        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = s.Session.Id, Delta = new Delta { Tick = 11, DeltaSeq = 11 } });

        await Read(host, LiveServerMsg.MOneofCase.ResyncRequest);
        Assert.True(guest.IsOpen);
    }
}
