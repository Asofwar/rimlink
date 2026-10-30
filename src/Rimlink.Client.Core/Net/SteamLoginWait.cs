using Rimlink.Protocol.V1;

namespace Rimlink.Client.Net;

/// <summary>
/// How long a login waits for Steam to accept a ticket: 2, 4, 8, then every 15 s, about two and a half minutes in all. Steam
/// refused new tickets for two minutes after a session ended (2026-10-30); only that refusal is retried, a server without a Steam
/// key answers with no code and goes to the account login at once. One wait per login.
/// </summary>
public sealed class SteamLoginWait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(15);

    private TimeSpan next = TimeSpan.FromSeconds(2);
    private TimeSpan waited = TimeSpan.Zero;

    /// <summary>The pause before trying again, or null when this refusal is not worth another try.</summary>
    public TimeSpan? Next(ErrorCode? refusal)
    {
        if (refusal != ErrorCode.SteamTicketRejected || waited >= Limit) return null;
        var delay = next;
        waited += delay;
        next = next + next < Longest ? next + next : Longest;
        return delay;
    }
}
