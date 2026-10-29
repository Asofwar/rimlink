using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Sessions;

public static class Activity
{
    /// <summary>The player's last activity: the later of the last login and the end of the last session.</summary>
    public static DateTime? Of(Account account) => Latest(account.LastSeenAt, account.LastSessionEndAt);

    public static DateTime? Latest(DateTime? lastSeen, DateTime? sessionEnd) =>
        sessionEnd is { } end && (lastSeen is not { } seen || end > seen) ? end : lastSeen;
}
