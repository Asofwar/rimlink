using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Admin;

public sealed record PanelSession(string Id, DateTimeOffset ExpiresAt);

/// <summary>
/// Web panel sessions, in memory only: a restart (needed anyway to change <c>Admin:ApiToken</c>) logs everyone out.
/// Also counts failed logins per IP for the lockout (admin panel spec §3).
/// </summary>
public sealed class PanelSessions(IOptions<PanelOptions> options, TimeProvider time)
{
    public const int MaxSessions = 20;
    public const int MaxFailures = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    /// <summary>Above this many tracked addresses, failures older than the window are dropped on the next failure.</summary>
    public const int MaxTrackedIps = 1000;

    private readonly Lock gate = new();
    private readonly Dictionary<string, PanelSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> failures = new(StringComparer.Ordinal);

    public PanelSession Create()
    {
        var now = time.GetUtcNow();
        var session = new PanelSession(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)), now + options.Value.SessionLength);
        lock (gate)
        {
            foreach (var expired in sessions.Values.Where(s => s.ExpiresAt <= now).ToList()) sessions.Remove(expired.Id);
            sessions[session.Id] = session;
            // Every session lasts as long, so the earliest expiry is the oldest login.
            while (sessions.Count > MaxSessions) sessions.Remove(sessions.Values.MinBy(s => s.ExpiresAt)!.Id);
        }
        return session;
    }

    public PanelSession? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var session)) return null;
            if (session.ExpiresAt > time.GetUtcNow()) return session;
            sessions.Remove(id);
            return null;
        }
    }

    public void End(string id)
    {
        lock (gate) sessions.Remove(id);
    }

    public bool IsLockedOut(string ip, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (!failures.TryGetValue(ip, out var entry)) return false;
            if (now - entry.Start >= FailureWindow)
            {
                failures.Remove(ip);
                return false;
            }
            if (entry.Count < MaxFailures) return false;
            retryAfter = entry.Start + FailureWindow - now;
            return true;
        }
    }

    internal int FailureEntries
    {
        get { lock (gate) return failures.Count; }
    }

    /// <summary>The window starts at the first failure; a successful login does not reset it.</summary>
    public void RecordFailure(string ip)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            // An address that never comes back would otherwise stay forever; the login endpoint is public.
            if (failures.Count >= MaxTrackedIps)
                foreach (var stale in failures.Where(f => now - f.Value.Start >= FailureWindow).Select(f => f.Key).ToList()) failures.Remove(stale);
            failures[ip] = failures.TryGetValue(ip, out var entry) && now - entry.Start < FailureWindow
                ? (entry.Start, entry.Count + 1)
                : (now, 1);
        }
    }
}
