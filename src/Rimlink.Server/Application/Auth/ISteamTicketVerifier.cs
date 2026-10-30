namespace Rimlink.Server.Application.Auth;

public sealed record SteamIdentity(long SteamId, long OwnerSteamId, bool VacBanned);

public interface ISteamTicketVerifier
{
    /// <summary>False on a server without a Steam key: no ticket can pass, and trying again cannot help.</summary>
    bool Configured { get; }

    /// <summary>Returns the identity behind a Steamworks session ticket, or null when the ticket is not valid.</summary>
    Task<SteamIdentity?> Verify(byte[] ticket, CancellationToken cancellationToken);

    /// <summary>The account's public Steam name, or null when Steam gives none. Never fails a login.</summary>
    Task<string?> PersonaName(long steamId, CancellationToken cancellationToken);
}
