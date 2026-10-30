namespace Rimlink.Client.Net;

/// <summary>
/// Whether a login sends the Steam ticket it already holds. Steam may confirm a ticket later than a login is willing to wait, and a new
/// ticket would start that wait over: an unconfirmed ticket is kept until it is confirmed or too old, and a confirmed one goes once more
/// if it was sent before Steam confirmed it. A confirmed ticket that was sent and refused is replaced.
/// </summary>
public static class SteamTicketReuse
{
    public static readonly TimeSpan MaxUnconfirmedAge = TimeSpan.FromSeconds(60);

    /// <param name="sentConfirmed">The ticket already went out after Steam confirmed it.</param>
    public static bool Keep(bool confirmed, TimeSpan age, bool sentConfirmed) =>
        confirmed ? !sentConfirmed : age < MaxUnconfirmedAge;
}
