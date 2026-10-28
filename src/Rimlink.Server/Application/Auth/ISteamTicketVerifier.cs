namespace Rimlink.Server.Application.Auth;

public sealed record SteamIdentity(long SteamId, long OwnerSteamId, bool VacBanned);

public interface ISteamTicketVerifier
{
    /// <summary>Returns the identity behind a Steamworks session ticket, or null when the ticket is not valid.</summary>
    Task<SteamIdentity?> Verify(byte[] ticket, CancellationToken cancellationToken);
}
