using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Sessions;

public static class Activity
{
    /// <summary>The player's last activity: the later of the last login and the end of the last session.</summary>
    public static DateTime? Of(Account account) =>
        account.LastSessionEndAt is { } end && (account.LastSeenAt is not { } seen || end > seen) ? end : account.LastSeenAt;
}
