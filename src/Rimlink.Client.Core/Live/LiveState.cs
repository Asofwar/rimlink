using Rimlink.Protocol.V1;

namespace Rimlink.Client.Live;

/// <summary>What the player sees of live sessions: the current one, an open invitation, the ends the host still owes. Fed by the event stream.</summary>
public sealed class LiveState
{
    public LiveSession? Current { get; private set; }
    public LiveInvite? PendingInvite { get; private set; }
    /// <summary>Sessions in Ending where this player is the host (from the login reply).</summary>
    public List<LiveSession> PendingEnds { get; } = [];

    public event Action<LiveInvite>? Invited;
    public event Action<LiveSession>? Started;
    public event Action<LiveSession>? Ended;
    public event Action<LiveSession>? FellBack;
    public event Action<long>? PeerLost;
    public event Action<long>? PeerBack;

    public void Load(IEnumerable<LiveSession> pendingEnds, LiveSession? current)
    {
        PendingEnds.Clear();
        PendingEnds.AddRange(pendingEnds);
        Current = current;
    }

    public void SetCurrent(LiveSession? session) => Current = session;

    public void ClearInvite() => PendingInvite = null;

    public void Apply(ServerEvent ev)
    {
        switch (ev.BodyCase)
        {
            case ServerEvent.BodyOneofCase.LiveInvite:
                PendingInvite = ev.LiveInvite;
                Invited?.Invoke(ev.LiveInvite);
                break;
            case ServerEvent.BodyOneofCase.LiveStarted:
                if (PendingInvite?.Session.Id == ev.LiveStarted.Session.Id) PendingInvite = null;
                Current = ev.LiveStarted.Session;
                Started?.Invoke(ev.LiveStarted.Session);
                break;
            case ServerEvent.BodyOneofCase.LiveEnded:
                if (PendingInvite?.Session.Id == ev.LiveEnded.Session.Id) PendingInvite = null;
                if (Current?.Id == ev.LiveEnded.Session.Id) Current = ev.LiveEnded.Session.State == LiveState_.Ended ? null : ev.LiveEnded.Session;
                Ended?.Invoke(ev.LiveEnded.Session);
                break;
            case ServerEvent.BodyOneofCase.LiveFallback:
                if (PendingInvite?.Session.Id == ev.LiveFallback.Session.Id) PendingInvite = null;
                if (Current?.Id == ev.LiveFallback.Session.Id) Current = null;
                FellBack?.Invoke(ev.LiveFallback.Session);
                break;
            case ServerEvent.BodyOneofCase.LivePeerLost:
                PeerLost?.Invoke(ev.LivePeerLost.SessionId);
                break;
            case ServerEvent.BodyOneofCase.LivePeerBack:
                PeerBack?.Invoke(ev.LivePeerBack.SessionId);
                break;
        }
    }

    public void Reset()
    {
        Current = null;
        PendingInvite = null;
        PendingEnds.Clear();
    }

    private static class LiveState_
    {
        public const Protocol.V1.LiveState Ended = Protocol.V1.LiveState.LiveEnded;
    }
}
