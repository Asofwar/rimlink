using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Live;

/// <summary>
/// Live session calls and the frame stream. <see cref="Open"/> keeps the stream alive in the background: Hello,
/// a heartbeat every 2 s, reconnection with back-off for up to <see cref="ReconnectBudget"/> after a drop; received
/// frames go to <see cref="Incoming"/> for the game's main thread. Sends are queued and rate-limited locally.
/// </summary>
public sealed class LiveClient(RimlinkConnection connection, ILog log)
{
    public const int HostRole = 1, GuestRole = 2;
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ReconnectBudget = TimeSpan.FromMinutes(5);
    private const int OutgoingCapacity = 256;

    private static DateTime Until => DateTime.UtcNow + Economy.EconomyClient.Deadline;

    private readonly object gate = new();
    private readonly Queue<LiveClientMsg> outgoing = new();
    private readonly SemaphoreSlim outgoingSignal = new(0);
    private readonly RateGate deltaRate = new(20), orderRate = new(30);
    private CancellationTokenSource? loop;
    private AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg>? call;
    private long sessionId;
    private int role, snapshotSeq;
    private DateTime lost = DateTime.MinValue;

    public FrameQueue Incoming { get; } = new();
    public bool IsOpen { get; private set; }
    public long SessionId => sessionId;

    public event Action<LiveEndReason>? Ended;
    public event Action? Reconnected;
    public event Action? PeerLost;
    public event Action? PeerBack;

    public Task<LiveSession> Propose(LiveProposal p, CancellationToken ct) => connection.LiveApi.ProposeAsync(p, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<LiveSession> Respond(long id, bool accept, CancellationToken ct) => connection.LiveApi.RespondAsync(new LiveAnswer { SessionId = id, Accept = accept }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task Cancel(long id, CancellationToken ct) => connection.LiveApi.CancelAsync(new LiveRef { SessionId = id }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<LiveSession> Current(CancellationToken ct) => connection.LiveApi.CurrentAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<PawnList> Pawns(long id, CancellationToken ct) => connection.LiveApi.PawnsAsync(new LiveRef { SessionId = id }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<PlayerFlags> SetAutoAccept(bool enabled, CancellationToken ct) => connection.LiveApi.SetAutoAcceptAsync(new AutoAcceptRequest { Enabled = enabled }, deadline: Until, cancellationToken: ct).ResponseAsync;

    /// <summary>Opens the stream and returns once the Hello went out; the stream then lives in the background until End, Close or the reconnect budget runs out.</summary>
    public async Task Open(long session, int asRole, int snapshot, CancellationToken ct)
    {
        await Close();
        sessionId = session;
        role = asRole;
        snapshotSeq = snapshot;
        Incoming.Clear();
        lock (gate)
        {
            outgoing.Clear();
            sentDeltaSeq = 0; // a host opens once per session; a guest opens again on every snapshot it loads
            receivedDeltaSeq = 0;
        }
        var cts = new CancellationTokenSource();
        loop = cts;
        var opened = new TaskCompletionSource();
        _ = Task.Run(() => Run(cts.Token, opened), CancellationToken.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        await opened.Task.WaitAsync(linked.Token);
    }

    /// <summary>
    /// Queues a frame, filling in the session id; false when it was dropped here (deltas and orders over the local rate, a delta on a
    /// full queue), with a log line. The host sends deltas through <see cref="SendDelta"/>, which never drops part of one.
    /// </summary>
    public bool Send(LiveClientMsg msg)
    {
        if (msg.SessionId == 0) msg.SessionId = sessionId;
        var now = DateTime.UtcNow;
        if (msg.MCase == LiveClientMsg.MOneofCase.Delta && !deltaRate.Take(now)) { log.Warn("live: delta dropped (local rate)"); return false; }
        if (msg.MCase == LiveClientMsg.MOneofCase.Order && !orderRate.Take(now)) { log.Warn("live: order dropped (local rate)"); return false; }
        lock (gate)
        {
            if (outgoing.Count >= OutgoingCapacity)
            {
                if (msg.MCase == LiveClientMsg.MOneofCase.Delta) { log.Warn("live: delta dropped (queue full)"); return false; }
                var kept = outgoing.Where(m => m.MCase != LiveClientMsg.MOneofCase.Delta).ToList();
                outgoing.Clear();
                foreach (var m in kept) outgoing.Enqueue(m);
            }
            outgoing.Enqueue(msg);
        }
        outgoingSignal.Release();
        return true;
    }

    /// <summary>
    /// Queues every frame of one delta or none of them (false: over the local rate or no room). A delta is a diff against what the
    /// peer has, so a sender that is refused keeps the changes for its next one; half a delta would leave the peer off in a way no
    /// later diff repairs.
    /// </summary>
    public bool SendDelta(IReadOnlyList<Delta> frames)
    {
        lock (gate)
        {
            if (outgoing.Count + frames.Count > OutgoingCapacity || !deltaRate.Take(DateTime.UtcNow, frames.Count)) return false;
            foreach (var frame in frames)
            {
                frame.DeltaSeq = ++sentDeltaSeq;
                outgoing.Enqueue(new LiveClientMsg { SessionId = sessionId, Delta = frame });
            }
        }
        outgoingSignal.Release(frames.Count);
        return true;
    }

    public static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(10);
    private DateTime lastResync = DateTime.MinValue;
    /// <summary>The host numbers the delta frames it queues; the guest remembers the last number it got (0: none yet since Open).</summary>
    private long sentDeltaSeq, receivedDeltaSeq;

    /// <summary>
    /// A delta from the host: a number that does not follow the last one means frames were lost on the way (the server's limit or
    /// queue, a reconnect), and the diffs that follow assume changes this mirror never saw.
    /// </summary>
    private void CheckDeltaSeq(Delta delta)
    {
        long seq = delta.DeltaSeq;
        if (seq == 0) return; // an older host
        long last = receivedDeltaSeq;
        receivedDeltaSeq = seq;
        if (last != 0 && seq != last + 1) RequestResync($"delta gap: {last} then {seq}");
    }

    /// <summary>
    /// Asks the host for a fresh snapshot, at most once per <see cref="ResyncInterval"/> (the server passes one per ten seconds,
    /// the host takes one per 30 s): every frame with a reason would otherwise ask again. False when one was asked too recently.
    /// </summary>
    public bool RequestResync(string why)
    {
        var now = DateTime.UtcNow;
        lock (gate)
        {
            if (now - lastResync < ResyncInterval) return false;
            lastResync = now;
        }
        log.Warn($"live: {why}; asking for a resync");
        Send(new LiveClientMsg { ResyncRequest = new Empty() });
        return true;
    }

    private Task? ending;

    /// <summary>Ends the session for both sides (the server relays the end and marks the session Ending), then closes. A Close that follows waits for the frame to leave.</summary>
    public Task End(LiveEndReason reason) => ending = EndCore(reason);

    private LiveEndReason? requestedEnd;

    private async Task EndCore(LiveEndReason reason)
    {
        if (loop is null) return;
        requestedEnd = reason;
        Send(new LiveClientMsg { End = new EndMsg { Reason = reason } });
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < until)
        {
            int left;
            lock (gate) left = outgoing.Count;
            if (left == 0) break;
            await Task.Delay(20);
        }
        await Task.Delay(150); // the write itself
        await CloseCore();
    }

    public async Task Close()
    {
        if (ending is { } pending)
        {
            ending = null;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        await CloseCore();
    }

    private async Task CloseCore()
    {
        var cts = loop;
        loop = null;
        if (cts is null) return;
        cts.Cancel();
        IsOpen = false;
        try { call?.Dispose(); } catch { }
        call = null;
        await Task.Yield();
    }

    /// <summary>Test hook: aborts the current transport so the loop takes the reconnect path, as after a network drop.</summary>
    public void DropTransportForTest() => call?.Dispose();

    private async Task Run(CancellationToken ct, TaskCompletionSource opened)
    {
        lost = DateTime.MinValue;
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> current;
            try
            {
                current = connection.LiveApi.Stream(cancellationToken: ct);
                call = current;
                await current.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, Hello = new Hello { Role = role, SnapshotSeq = snapshotSeq } }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!first) { if (await Backoff(ct, ex.Message)) continue; return; }
                opened.TrySetException(ex);
                return;
            }
            IsOpen = true;
            requestedEnd = null;
            if (first) opened.TrySetResult();
            else { lost = DateTime.MinValue; log.Info("live: stream reconnected"); Reconnected?.Invoke(); }
            first = false;

            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var writer = Pump(current, streamCts.Token);
            var beat = Heartbeats(streamCts.Token);
            string? ended = null;
            try
            {
                while (await current.ResponseStream.MoveNext(streamCts.Token))
                {
                    var msg = current.ResponseStream.Current;
                    Incoming.Enqueue(msg);
                    switch (msg.MCase)
                    {
                        case LiveServerMsg.MOneofCase.Delta: CheckDeltaSeq(msg.Delta); break;
                        case LiveServerMsg.MOneofCase.PeerLost: PeerLost?.Invoke(); break;
                        case LiveServerMsg.MOneofCase.PeerBack: PeerBack?.Invoke(); break;
                        case LiveServerMsg.MOneofCase.End: ended = msg.End.Reason.ToString(); break;
                    }
                    if (ended is not null) break;
                }
                // A stream that completes without an End frame is a drop (a relay timeout, a server restart): the reconnect's Hello learns
                // whether the session is gone. After our own End the server closes the stream: that is the end we asked for.
                if (ended is null && requestedEnd is { } requested) ended = requested.ToString();
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Aborted) { ended = "replaced"; }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition || ex.StatusCode == StatusCode.PermissionDenied) { ended = ex.Status.Detail; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.Warn($"live: stream lost: {ex.Message}");
            }
            finally
            {
                streamCts.Cancel();
                try { await writer; } catch { }
                try { await beat; } catch { }
                try { current.Dispose(); } catch { }
            }
            if (ct.IsCancellationRequested) return;
            if (ended is not null)
            {
                IsOpen = false;
                log.Info($"live: session over ({ended})");
                loop = null;
                Ended?.Invoke(Enum.TryParse<LiveEndReason>(ended, out var reason) ? reason : LiveEndReason.EndNormal);
                return;
            }
            IsOpen = false;
            if (!await Backoff(ct, "dropped")) return;
        }
    }

    private async Task<bool> Backoff(CancellationToken ct, string why)
    {
        if (lost == DateTime.MinValue) lost = DateTime.UtcNow;
        var spent = DateTime.UtcNow - lost;
        if (spent > ReconnectBudget)
        {
            log.Warn("live: reconnect budget spent; the session is lost");
            loop = null;
            Ended?.Invoke(role == GuestRole ? LiveEndReason.EndGuestLost : LiveEndReason.EndHostLost);
            return false;
        }
        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, spent.TotalSeconds / 4)));
        log.Warn($"live: stream {why}, retrying in {delay.TotalSeconds:0}s");
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return false; }
        return !ct.IsCancellationRequested;
    }

    private async Task Pump(AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> current, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await outgoingSignal.WaitAsync(ct);
                LiveClientMsg? msg;
                lock (gate) msg = outgoing.Count > 0 ? outgoing.Dequeue() : null;
                if (msg is null) continue;
                await current.RequestStream.WriteAsync(msg, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.Warn($"live: send failed: {ex.Message}"); }
    }

    private async Task Heartbeats(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Heartbeat, ct);
                Send(new LiveClientMsg { Heartbeat = new Empty() });
            }
        }
        catch (OperationCanceledException) { }
    }

    private sealed class RateGate(int perSecond)
    {
        private double tokens = perSecond;
        private DateTime last = DateTime.MinValue;

        public bool Take(DateTime now, int count = 1)
        {
            lock (this)
            {
                if (last != DateTime.MinValue) tokens = Math.Min(perSecond, tokens + (now - last).TotalSeconds * perSecond);
                last = now;
                if (tokens < count) return false;
                tokens -= count;
                return true;
            }
        }
    }
}
