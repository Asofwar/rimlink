using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Sessions;

/// <summary>Writes <c>Account.LastSessionEndAt</c> when a session ends: the baseline of the away summary and of inactivity.</summary>
public sealed class SessionEndRecorder : IDisposable
{
    private readonly SessionRegistry sessions;
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider time;
    private readonly ILogger<SessionEndRecorder> log;

    public SessionEndRecorder(SessionRegistry sessions, IServiceScopeFactory scopes, TimeProvider time, ILogger<SessionEndRecorder> log)
    {
        this.sessions = sessions;
        this.scopes = scopes;
        this.time = time;
        this.log = log;
        sessions.SessionEnded += OnEnded;
    }

    private void OnEnded(Session session) => _ = Record(session);

    private async Task Record(Session session)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            var now = time.GetUtcNow().UtcDateTime;
            await db.Accounts.Where(a => a.Id == session.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSessionEndAt, now));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not record the session end of {Player}", session.PlayerName);
        }
    }

    public void Dispose() => sessions.SessionEnded -= OnEnded;
}
