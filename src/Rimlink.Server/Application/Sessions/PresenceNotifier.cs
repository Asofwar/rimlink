using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Application.Feed;

namespace Rimlink.Server.Application.Sessions;

/// <summary>Turns session start/end into PlayerJoined/PlayerLeft events and system chat lines.</summary>
public sealed class PresenceNotifier : IDisposable
{
    private readonly SessionRegistry sessions;
    private readonly EventBus events;
    private readonly AdminFeed feed;
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<PresenceNotifier> log;

    public PresenceNotifier(SessionRegistry sessions, EventBus events, AdminFeed feed, IServiceScopeFactory scopes, ILogger<PresenceNotifier> log)
    {
        this.feed = feed;
        this.sessions = sessions;
        this.events = events;
        this.scopes = scopes;
        this.log = log;
        sessions.SessionStarted += OnStarted;
        sessions.SessionEnded += OnEnded;
    }

    private void OnStarted(Session session) => _ = Announce(session, joined: true);

    private void OnEnded(Session session) => _ = Announce(session, joined: false);

    private async Task Announce(Session session, bool joined)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == session.PlayerId);
            var reference = player?.ToRef() ?? new PlayerRef { PlayerId = session.PlayerId, Name = session.PlayerName };

            events.BroadcastExcept(session.PlayerId, joined
                ? new ServerEvent { PlayerJoined = new PlayerJoined { Player = reference } }
                : new ServerEvent { PlayerLeft = new PlayerLeft { Player = reference } });
            feed.Publish(joined ? FeedTypes.PlayerJoined : FeedTypes.PlayerLeft, new PlayerData(new FeedPlayer(reference.PlayerId, reference.Name), player?.FactionName ?? ""));
            await scope.ServiceProvider.GetRequiredService<ChatService>().System(joined ? $"{reference.Name} joined the server" : $"{reference.Name} left the server");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Presence announcement failed for {Player}", session.PlayerName);
        }
    }

    public void Dispose()
    {
        sessions.SessionStarted -= OnStarted;
        sessions.SessionEnded -= OnEnded;
    }
}
