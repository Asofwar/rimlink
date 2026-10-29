using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using LiveSession = Rimlink.Protocol.V1.LiveSession;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.LiveFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Live;

/// <summary>Spec §6: two real streams through the relay — routing, limits, queue overflow, heartbeats, reconnection.</summary>
public class LiveRelayTests
{
    private sealed record Pair(ServerFixture Fixture, LiveClients Nova, LiveClients Bob, LiveSession Session);

    /// <summary>Nova (guest) and Bob (host, auto-accept) with a session already Starting.</summary>
    private static async Task<Pair> Started(ServerFixture? fixture = null, LiveKind kind = LiveKind.LiveRaid)
    {
        fixture ??= LiveServer();
        var nova = await Connected(fixture, "nova");
        var bob = await Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.PlayerId);
        await bob.Live.SetAutoAcceptAsync(new AutoAcceptRequest { Enabled = true }, bob.Auth);
        var session = await nova.Live.ProposeAsync(Propose(bob.PlayerId, kind), nova.Auth);
        return new Pair(fixture, nova, bob, session);
    }

    private static async Task<AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg>> Open(LiveClients c, long sessionId, int role, int snapshotSeq = 0)
    {
        var call = c.Live.Stream(c.Auth);
        await call.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, Hello = new Hello { Role = role, SnapshotSeq = snapshotSeq } });
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

    private static async Task<List<LiveServerMsg>> Drain(AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> call, TimeSpan quiet)
    {
        var frames = new List<LiveServerMsg>();
        try
        {
            using var cts = new CancellationTokenSource(quiet);
            while (await call.ResponseStream.MoveNext(cts.Token)) frames.Add(call.ResponseStream.Current);
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        return frames;
    }

    private static async Task<LiveStateDb> StateOf(ServerFixture fixture, long id)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().LiveSessions.AsNoTracking().SingleAsync(s => s.Id == id)).State;
    }

    [Fact]
    public async Task DeltaReachesOnlyThePeer()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        using var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);

        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Delta = new Delta { Tick = 60, Speed = 1 } });
        await guest.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Order = new Order { Seq = 1, PawnId = 7, Draft = true } });

        Assert.Equal(60, (await Read(guest, LiveServerMsg.MOneofCase.Delta)).Delta.Tick);
        Assert.Equal(1, (await Read(host, LiveServerMsg.MOneofCase.Order)).Order.Seq);
        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, OrderResult = new OrderResult { Seq = 1, Ok = true } });
        Assert.True((await Read(guest, LiveServerMsg.MOneofCase.OrderResult)).OrderResult.Ok);
        Assert.DoesNotContain(await Drain(host, TimeSpan.FromMilliseconds(500)), f => f.MCase == LiveServerMsg.MOneofCase.Delta); // the host never gets its own delta back
    }

    [Fact]
    public async Task GuestHelloMarksRunningAndHostSeesPeerHello()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        Assert.Equal(LiveStateDb.Starting, await StateOf(fixture, p.Session.Id));

        using var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole, snapshotSeq: 3);

        var hello = await Read(host, LiveServerMsg.MOneofCase.PeerHello);
        Assert.Equal(LiveRelay.GuestRole, hello.PeerHello.Role);
        Assert.Equal(3, hello.PeerHello.SnapshotSeq);
        Assert.Equal(LiveStateDb.Running, await StateOf(fixture, p.Session.Id));
        Assert.Equal(LiveState.LiveRunning, (await p.Nova.Live.CurrentAsync(new Empty(), p.Nova.Auth)).State);
    }

    [Fact]
    public async Task SnapshotReadyReachesAGuestThatAttachesLater()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, SnapshotReady = new SnapshotReady { SaveVersion = 12 } });
        await Task.Delay(300);

        using var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);

        Assert.Equal(12, (await Read(guest, LiveServerMsg.MOneofCase.SnapshotReady)).SnapshotReady.SaveVersion);
        Assert.Equal(12, (await p.Nova.Live.CurrentAsync(new Empty(), p.Nova.Auth)).SnapshotVersion);
    }

    [Fact]
    public async Task OversizedFrameIsDroppedWithANotice()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        using var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);
        var big = new Delta { Tick = 6 };
        big.GuestPawnBlobs.Add(new PawnBlob { KindDef = "Colonist", Name = "Huge", ScribeXmlZstd = ByteString.CopyFrom(new byte[300 * 1024]) });

        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Delta = big });
        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Delta = new Delta { Tick = 12 } });

        Assert.Contains("bytes", (await Read(host, LiveServerMsg.MOneofCase.Notice)).Notice.Text);
        Assert.Equal(12, (await Read(guest, LiveServerMsg.MOneofCase.Delta)).Delta.Tick); // the small one still arrives, the big one never
    }

    [Fact]
    public async Task TooManyDeltasAreDropped()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        using var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);

        for (int i = 1; i <= 25; i++) await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Delta = new Delta { Tick = i } });

        Assert.Contains("deltas per second", (await Read(host, LiveServerMsg.MOneofCase.Notice)).Notice.Text);
        var delivered = (await Drain(guest, TimeSpan.FromSeconds(1))).Count(f => f.MCase == LiveServerMsg.MOneofCase.Delta);
        Assert.InRange(delivered, 1, 20);
    }

    [Fact]
    public void OverflowDropsOldDeltasNotOrders()
    {
        var queue = new RelayQueue();

        for (int i = 1; i <= 100; i++) queue.Enqueue(new LiveServerMsg { Delta = new Delta { Tick = i } });
        queue.Enqueue(new LiveServerMsg { Order = new Order { Seq = 1 } });
        queue.Enqueue(new LiveServerMsg { Delta = new Delta { Tick = 101 } });

        Assert.Equal(RelayQueue.Capacity, queue.Count);
        var frames = new List<LiveServerMsg>();
        while (queue.Count > 0) frames.Add(queue.ReadAsync(CancellationToken.None).Result!);
        Assert.Single(frames, f => f.MCase == LiveServerMsg.MOneofCase.Order);
        Assert.Equal(101, frames.Last().Delta.Tick);
        Assert.Equal(39, frames.First().Delta.Tick); // 100 deltas keep 37..100; the order evicts 37, delta 101 evicts 38
    }

    [Fact]
    public async Task HeartbeatTimeoutMarksWaitingAndReconnectResumes()
    {
        var p = await Started(LiveServer().WithSetting("Live:HeartbeatTimeoutSeconds", "1"));
        using var fixture = p.Fixture;
        using var bobEvents = p.Bob.Subscribe();
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);
        var keepAlive = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++) { await Task.Delay(400); try { await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Heartbeat = new Empty() }); } catch { return; } }
        });

        // The guest goes silent (no heartbeat) without closing anything.
        Assert.Equal(LiveServerMsg.MOneofCase.PeerLost, (await Read(host, LiveServerMsg.MOneofCase.PeerLost, TimeSpan.FromSeconds(8))).MCase);
        Assert.Equal(p.Session.Id, (await ReadEvent(bobEvents, ServerEvent.BodyOneofCase.LivePeerLost, e => e.LivePeerLost)).SessionId);
        Assert.Equal(LiveStateDb.Waiting, await StateOf(fixture, p.Session.Id));
        var closed = await Assert.ThrowsAsync<RpcException>(() => Read(guest, LiveServerMsg.MOneofCase.Delta, TimeSpan.FromSeconds(5)));
        Assert.Equal(StatusCode.Unavailable, closed.StatusCode); // a timed-out stream is told so: the client reconnects instead of taking it for a normal end
        guest.Dispose();

        using var again = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole, snapshotSeq: 1);
        Assert.Equal(LiveServerMsg.MOneofCase.PeerBack, (await Read(host, LiveServerMsg.MOneofCase.PeerBack)).MCase);
        Assert.Equal(p.Session.Id, (await ReadEvent(bobEvents, ServerEvent.BodyOneofCase.LivePeerBack, e => e.LivePeerBack)).SessionId);
        Assert.Equal(LiveStateDb.Running, await StateOf(fixture, p.Session.Id));
        await keepAlive;
    }

    [Fact]
    public async Task AGuestHelloDoesNotCancelAHostLoss()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var novaEvents = p.Nova.Subscribe();
        var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        using var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);
        Assert.Equal(LiveStateDb.Running, await StateOf(fixture, p.Session.Id));

        host.Dispose();
        Assert.Equal(LiveServerMsg.MOneofCase.PeerLost, (await Read(guest, LiveServerMsg.MOneofCase.PeerLost)).MCase);
        Assert.Equal(p.Session.Id, (await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LivePeerLost, e => e.LivePeerLost)).SessionId);
        Assert.Equal(LiveStateDb.Waiting, await StateOf(fixture, p.Session.Id));

        // The guest's network blinked: its new Hello must not mark the session running again while the host is still gone.
        guest.Dispose();
        using var again = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole, snapshotSeq: 1);
        await Task.Delay(500);
        Assert.Equal(LiveStateDb.Waiting, await StateOf(fixture, p.Session.Id));
        Assert.Empty(await Drain(again, TimeSpan.FromSeconds(1)).ContinueWith(t => t.Result.Where(f => f.MCase == LiveServerMsg.MOneofCase.PeerBack).ToList()));

        using var hostBack = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        Assert.Equal(p.Session.Id, (await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LivePeerBack, e => e.LivePeerBack)).SessionId);
        Assert.Equal(LiveStateDb.Running, await StateOf(fixture, p.Session.Id));
    }

    [Fact]
    public void ResyncRequestsAreRateLimited()
    {
        var limiter = new FrameLimiter(new Rimlink.Server.Hosting.LiveOptions());
        var now = DateTime.UtcNow;
        Assert.Null(limiter.Refuse(LiveClientMsg.MOneofCase.ResyncRequest, 10, now));
        Assert.Null(limiter.Refuse(LiveClientMsg.MOneofCase.ResyncRequest, 10, now));
        Assert.NotNull(limiter.Refuse(LiveClientMsg.MOneofCase.ResyncRequest, 10, now)); // two in a burst, then one per ten seconds
        Assert.NotNull(limiter.Refuse(LiveClientMsg.MOneofCase.ResyncRequest, 10, now + TimeSpan.FromSeconds(5)));
        Assert.Null(limiter.Refuse(LiveClientMsg.MOneofCase.ResyncRequest, 10, now + TimeSpan.FromSeconds(11)));
        Assert.Null(limiter.Refuse(LiveClientMsg.MOneofCase.Heartbeat, 10, now)); // heartbeats are never limited
    }

    [Fact]
    public async Task ASecondStreamOfTheSamePlayerReplacesTheFirst()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        using var first = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);

        using var second = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);
        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Delta = new Delta { Tick = 6 } });

        Assert.Equal(6, (await Read(second, LiveServerMsg.MOneofCase.Delta)).Delta.Tick);
        var closed = await Assert.ThrowsAsync<RpcException>(() => Read(first, LiveServerMsg.MOneofCase.Delta, TimeSpan.FromSeconds(5)));
        Assert.Equal(StatusCode.Aborted, closed.StatusCode);
        Assert.Equal(LiveStateDb.Running, await StateOf(fixture, p.Session.Id)); // a replacement is not a loss
    }

    [Fact]
    public async Task EndFromTheHostReachesTheGuestAndEndsTheSession()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var novaEvents = p.Nova.Subscribe();
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        using var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);

        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, End = new EndMsg { Reason = LiveEndReason.EndNormal } });

        Assert.Equal(LiveEndReason.EndNormal, (await Read(guest, LiveServerMsg.MOneofCase.End)).End.Reason);
        Assert.Equal(LiveEndReason.EndNormal, (await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveEnded, e => e.LiveEnded)).Session.EndReason);
        Assert.Equal(LiveStateDb.Ending, await StateOf(fixture, p.Session.Id));
        Assert.False(await guest.ResponseStream.MoveNext(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)); // the stream is over
    }

    [Fact]
    public async Task TheHostEndingForLackOfGuestPawnsTellsTheGuestWhy()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var novaEvents = p.Nova.Subscribe();
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        using var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);

        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, End = new EndMsg { Reason = LiveEndReason.EndGuestDefeated } });

        Assert.Equal(LiveEndReason.EndGuestDefeated, (await Read(guest, LiveServerMsg.MOneofCase.End)).End.Reason);
        Assert.Equal(LiveEndReason.EndGuestDefeated, (await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveEnded, e => e.LiveEnded)).Session.EndReason);
        Assert.Equal(LiveStateDb.Ending, await StateOf(fixture, p.Session.Id)); // the host's report still settles it
    }

    [Fact]
    public async Task ResyncDuringGuestPauseKeepsThePause()
    {
        var p = await Started();
        using var fixture = p.Fixture;
        using var host = await Open(p.Bob, p.Session.Id, LiveRelay.HostRole);
        using var guest = await Open(p.Nova, p.Session.Id, LiveRelay.GuestRole);
        await Read(host, LiveServerMsg.MOneofCase.PeerHello);
        await guest.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Order = new Order { Seq = 1, Pause = true } });
        Assert.True((await Read(host, LiveServerMsg.MOneofCase.Order)).Order.Pause);
        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Delta = new Delta { Tick = 600, PausedBy = 2 } });
        Assert.Equal(2, (await Read(guest, LiveServerMsg.MOneofCase.Delta)).Delta.PausedBy);

        await guest.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, ResyncRequest = new Empty() });

        Assert.Equal(LiveServerMsg.MOneofCase.ResyncRequest, (await Read(host, LiveServerMsg.MOneofCase.ResyncRequest)).MCase);
        await host.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, SnapshotReady = new SnapshotReady { SaveVersion = 5 } });
        Assert.Equal(5, (await Read(guest, LiveServerMsg.MOneofCase.SnapshotReady)).SnapshotReady.SaveVersion);
        Assert.Equal(LiveStateDb.Running, await StateOf(fixture, p.Session.Id)); // the pause is the host's game state; the relay changed nothing
    }

    [Fact]
    public async Task AStreamNeedsAnOpenSessionAndTheRightRole()
    {
        var p = await Started();
        using var fixture = p.Fixture;

        using var wrongRole = p.Nova.Live.Stream(p.Nova.Auth);
        await wrongRole.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Hello = new Hello { Role = LiveRelay.HostRole } });
        var denied = await Assert.ThrowsAsync<RpcException>(() => wrongRole.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        await p.Bob.Live.CancelAsync(new LiveRef { SessionId = p.Session.Id }, p.Bob.Auth); // Ending: no more streams
        using var late = p.Bob.Live.Stream(p.Bob.Auth);
        await late.RequestStream.WriteAsync(new LiveClientMsg { SessionId = p.Session.Id, Hello = new Hello { Role = LiveRelay.HostRole } });
        var gone = await Assert.ThrowsAsync<RpcException>(() => late.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(ErrorCode.LiveGone, gone.ErrorDetail()!.Code);
    }
}
