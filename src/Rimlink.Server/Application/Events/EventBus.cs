using Google.Protobuf.WellKnownTypes;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;

namespace Rimlink.Server.Application.Events;

/// <summary>Fan-out of server events to live sessions. Never blocks the caller.</summary>
public sealed class EventBus(SessionRegistry sessions, ILogger<EventBus> log)
{
    private long sequence;

    public void Broadcast(ServerEvent ev) => Deliver(ev, sessions.All);

    public void BroadcastExcept(long playerId, ServerEvent ev) => Deliver(ev, sessions.All.Where(s => s.PlayerId != playerId));

    public void BroadcastExcept(IReadOnlySet<long> playerIds, ServerEvent ev) => Deliver(ev, sessions.All.Where(s => !playerIds.Contains(s.PlayerId)));

    public void SendTo(long playerId, ServerEvent ev)
    {
        var target = sessions.ByPlayer(playerId);
        if (target is not null) Deliver(ev, [target]);
    }

    private void Deliver(ServerEvent ev, IEnumerable<Session> targets)
    {
        ev.Sequence = Interlocked.Increment(ref sequence);
        ev.At ??= Timestamp.FromDateTime(DateTime.UtcNow);

        foreach (var session in targets)
        {
            if (session.Events.TryWrite(ev)) continue;

            log.LogWarning("Dropping slow consumer {Player} ({AccountId}): event queue full", session.PlayerName, session.AccountId);
            sessions.End(session.Token);
        }
    }
}
