namespace Rimlink.Client.Net;

/// <summary>
/// The offer to leave to the menu during a long outage comes once per outage. An outage is known by when it began
/// (<see cref="RimlinkConnection.ReconnectingSince"/>), so a later one, also on a new connection in the same game, is offered again.
/// </summary>
public sealed class ExitOffer
{
    private DateTime? offeredFor;

    /// <summary>True when the offer should open now; it then counts as made for this outage.</summary>
    public bool Due(DateTime? reconnectingSince, bool outageIsLong)
    {
        if (!outageIsLong || reconnectingSince is not { } since || offeredFor == since) return false;
        offeredFor = since;
        return true;
    }
}
