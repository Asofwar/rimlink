using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Live;

/// <summary>One participant's attachment to a live session: the frames waiting for it and when it was last heard from.</summary>
public sealed class LiveChannel(long sessionId, long playerId, int role, LiveOptions options)
{
    public long SessionId { get; } = sessionId;
    public long PlayerId { get; } = playerId;
    /// <summary>1 host, 2 guest.</summary>
    public int Role { get; } = role;
    public RelayQueue Queue { get; } = new();
    public FrameLimiter Limiter { get; } = new(options);
    public DateTime LastFrameAt { get; set; } = DateTime.UtcNow;
    private volatile bool handling;
    /// <summary>Set while the relay handles one of this channel's frames: the heartbeat sweep never times a channel out mid-frame.</summary>
    public bool Handling { get => handling; set => handling = value; }
    /// <summary>Set after this participant was told its peer is gone, so the peer's return is announced once.</summary>
    public bool PeerLostSent { get; set; }
    /// <summary>Why the channel was closed by the relay: "replaced", "ended" or "timeout" (null while open or closed by the client).</summary>
    public string? ClosedBy { get; set; }
    /// <summary>The guest's latest Hello, for a host that attaches after it (read and written under the relay's lock).</summary>
    public Hello? Hello { get; set; }
    public CancellationTokenSource Closed { get; } = new();
}

/// <summary>
/// Routes frames between the two participants of a live session without interpreting them (spec §6): size and
/// rate limits, a bounded queue per receiver, peer-lost/peer-back on detach and reattach, and the session state
/// changes a Hello, an End or a lost heartbeat imply.
/// </summary>
public sealed class LiveRelay(IServiceScopeFactory scopes, IOptions<LiveOptions> options, LiveTimeoutService timeouts, ILogger<LiveRelay> log)
{
    public const int HostRole = 1, GuestRole = 2;

    private readonly Dictionary<(long Session, long Player), LiveChannel> channels = [];
    private readonly Dictionary<long, SnapshotReady> snapshots = [];
    private readonly object gate = new();
    private bool sweeping;

    /// <summary>
    /// A second stream of the same player replaces the first; a returning peer is announced to the other side. A host that attaches
    /// after the guest gets its Hello now: nothing else lifts the host's wait for the guest.
    /// </summary>
    public LiveChannel Attach(long sessionId, long playerId, int role)
    {
        EnsureSweep();
        var channel = new LiveChannel(sessionId, playerId, role, options.Value);
        LiveChannel? replaced, peer;
        SnapshotReady? snapshot;
        lock (gate)
        {
            channels.TryGetValue((sessionId, playerId), out replaced);
            channels[(sessionId, playerId)] = channel;
            peer = PeerOf(channel);
            snapshots.TryGetValue(sessionId, out snapshot);
            if (role == HostRole && peer?.Hello is { } guestHello) channel.Queue.Enqueue(new LiveServerMsg { PeerHello = guestHello });
            if (peer is { PeerLostSent: true })
            {
                peer.PeerLostSent = false;
                peer.Queue.Enqueue(new LiveServerMsg { PeerBack = new Empty() });
            }
        }
        if (replaced is not null) Close(replaced, "replaced");
        if (role == GuestRole && snapshot is not null) channel.Queue.Enqueue(new LiveServerMsg { SnapshotReady = snapshot }); // the host announced it before the guest attached
        log.LogInformation("Live session {Session}: player {Player} attached as {Role}", sessionId, playerId, role == HostRole ? "host" : "guest");
        return channel;
    }

    /// <summary>The client closed its stream or stopped answering: the peer waits for it, the session goes Waiting.</summary>
    public async Task Detach(LiveChannel channel, string reason)
    {
        lock (gate)
        {
            if (!channels.TryGetValue((channel.SessionId, channel.PlayerId), out var current) || current != channel) return; // replaced or ended already
            channels.Remove((channel.SessionId, channel.PlayerId));
            if (PeerOf(channel) is { PeerLostSent: false } peer)
            {
                peer.PeerLostSent = true;
                peer.Queue.Enqueue(new LiveServerMsg { PeerLost = new Empty() });
            }
        }
        channel.ClosedBy ??= reason;
        channel.Queue.Complete();
        channel.Closed.Cancel();
        log.LogInformation("Live session {Session}: player {Player} detached ({Reason})", channel.SessionId, channel.PlayerId, reason);
        using var scope = scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<LiveSessionService>();
        await sessions.MarkWaiting(channel.SessionId, channel.PlayerId);
        bool back;
        lock (gate) back = channels.ContainsKey((channel.SessionId, channel.PlayerId));
        if (back) await MarkRunning(channel); // a new stream of the same player arrived while this one was being closed (a new scope: the row must be read afresh)
    }

    /// <summary>
    /// Routes one frame from a participant; the answer (a notice for a refused frame) goes back on the sender's own queue. The time the
    /// server spends on the frame (a slow database under a Hello) is not the client's silence: a channel is never timed out mid-frame.
    /// </summary>
    public async Task Forward(LiveChannel from, LiveClientMsg msg, int size)
    {
        from.LastFrameAt = DateTime.UtcNow;
        from.Handling = true;
        try { await Route(from, msg, size); }
        finally
        {
            from.LastFrameAt = DateTime.UtcNow;
            from.Handling = false;
        }
    }

    private async Task Route(LiveChannel from, LiveClientMsg msg, int size)
    {
        LiveMetrics.Frames.WithLabels(from.Role == HostRole ? "host" : "guest").Inc();
        if (from.Limiter.Refuse(msg.MCase, size, from.LastFrameAt) is { } refusal)
        {
            from.Queue.Enqueue(new LiveServerMsg { Notice = new LiveNotice { Text = "frame dropped: " + refusal } });
            return;
        }
        switch (msg.MCase)
        {
            case LiveClientMsg.MOneofCase.Heartbeat:
                return;
            case LiveClientMsg.MOneofCase.Hello:
                // The guest's first Hello starts the run (the state is Running before the host sees PeerHello); a lost side's Hello ends a wait.
                if (from.Role == GuestRole) { await MarkRunning(from); GuestHello(from, msg.Hello); }
                else { ToPeer(from, new LiveServerMsg { PeerHello = msg.Hello }); await MarkRunning(from); }
                return;
            case LiveClientMsg.MOneofCase.SnapshotReady:
                using (var scope = scopes.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<LiveSessionService>().SetSnapshot(from.SessionId, msg.SnapshotReady.SaveVersion);
                lock (gate) snapshots[from.SessionId] = msg.SnapshotReady; // on record first: the guest reads the session once it hears
                ToPeer(from, new LiveServerMsg { SnapshotReady = msg.SnapshotReady });
                return;
            case LiveClientMsg.MOneofCase.End:
                await End(from, msg.End.Reason);
                return;
            case LiveClientMsg.MOneofCase.Delta: ToPeer(from, new LiveServerMsg { Delta = msg.Delta }); return;
            case LiveClientMsg.MOneofCase.Order: ToPeer(from, new LiveServerMsg { Order = msg.Order }); return;
            case LiveClientMsg.MOneofCase.OrderResult: ToPeer(from, new LiveServerMsg { OrderResult = msg.OrderResult }); return;
            case LiveClientMsg.MOneofCase.ResyncRequest: ToPeer(from, new LiveServerMsg { ResyncRequest = new Empty() }); return;
        }
    }

    /// <summary>Either side ended the session: the peer gets the end frame, both channels close, the session goes Ending.</summary>
    public async Task End(LiveChannel from, LiveEndReason reason)
    {
        LiveChannel? peer;
        lock (gate)
        {
            peer = PeerOf(from);
            channels.Remove((from.SessionId, from.PlayerId));
            if (peer is not null) channels.Remove((peer.SessionId, peer.PlayerId));
            snapshots.Remove(from.SessionId);
        }
        peer?.Queue.Enqueue(new LiveServerMsg { End = new EndMsg { Reason = reason } });
        using (var scope = scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<LiveSessionService>().MarkEnding(from.SessionId, reason);
        Close(from, "ended");
        if (peer is not null) Close(peer, "ended");
    }

    /// <summary>Ends a session from outside the streams (admin, timeouts): both channels get the end frame and close.</summary>
    public void CloseSession(long sessionId, LiveEndReason reason)
    {
        List<LiveChannel> both;
        lock (gate)
        {
            both = channels.Values.Where(c => c.SessionId == sessionId).ToList();
            foreach (var c in both) channels.Remove((c.SessionId, c.PlayerId));
            snapshots.Remove(sessionId);
        }
        foreach (var c in both)
        {
            c.Queue.Enqueue(new LiveServerMsg { End = new EndMsg { Reason = reason } });
            Close(c, "ended");
        }
    }

    private async Task MarkRunning(LiveChannel from)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LiveSessionService>().MarkRunning(from.SessionId, from.PlayerId);
    }

    public bool IsAttached(long sessionId, long playerId)
    {
        lock (gate) return channels.ContainsKey((sessionId, playerId));
    }

    public LiveChannel? ChannelOf(long sessionId, long playerId)
    {
        lock (gate) return channels.GetValueOrDefault((sessionId, playerId));
    }

    /// <summary>Tells the host, or keeps the Hello for <see cref="Attach"/> when it is not there yet; the lock makes it exactly one of the two.</summary>
    private void GuestHello(LiveChannel guest, Hello hello)
    {
        lock (gate)
        {
            guest.Hello = hello;
            PeerOf(guest)?.Queue.Enqueue(new LiveServerMsg { PeerHello = hello });
        }
    }

    private void ToPeer(LiveChannel from, LiveServerMsg msg)
    {
        LiveChannel? peer;
        lock (gate) peer = PeerOf(from);
        if (peer is null) return; // nobody to hear it yet; a delta before the guest attaches is stale anyway
        if (!peer.Queue.Enqueue(msg)) log.LogDebug("Live session {Session}: a frame for player {Player} was dropped (queue full)", from.SessionId, peer.PlayerId);
    }

    private LiveChannel? PeerOf(LiveChannel c) => channels.Values.FirstOrDefault(x => x.SessionId == c.SessionId && x.PlayerId != c.PlayerId);

    private static void Close(LiveChannel channel, string reason)
    {
        channel.ClosedBy ??= reason;
        channel.Queue.Complete();
        channel.Closed.Cancel();
    }

    private void EnsureSweep()
    {
        lock (gate)
        {
            if (sweeping) return;
            sweeping = true;
        }
        timeouts.Sweep += SweepHeartbeats;
    }

    /// <summary>A channel silent for HeartbeatTimeoutSeconds is detached: its peer waits, the session goes Waiting.</summary>
    private async Task SweepHeartbeats(DateTime now)
    {
        var limit = TimeSpan.FromSeconds(options.Value.HeartbeatTimeoutSeconds);
        List<LiveChannel> silent;
        lock (gate) silent = channels.Values.Where(c => !c.Handling && now - c.LastFrameAt > limit).ToList();
        foreach (var c in silent) await Detach(c, "timeout");
    }
}
