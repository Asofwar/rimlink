using System.Collections.Concurrent;
using System.Text;
using Rimlink.Server.Application.Auth;

namespace Rimlink.Server.Tests.Support;

/// <summary>Accepts tickets of the form "steam:&lt;steamid&gt;[:&lt;ownersteamid&gt;]" (UTF-8); anything else is invalid.</summary>
public sealed class FakeSteamVerifier : ISteamTicketVerifier
{
    public bool Configured { get; set; } = true;

    /// <summary>Steam names by SteamID; an id not listed has none.</summary>
    public static readonly ConcurrentDictionary<long, string> Personas = new();

    public Task<string?> PersonaName(long steamId, CancellationToken cancellationToken) =>
        Task.FromResult(Personas.TryGetValue(steamId, out var name) ? name : null);

    public static byte[] Ticket(long steamId, long? ownerSteamId = null) =>
        Encoding.UTF8.GetBytes(ownerSteamId is null ? $"steam:{steamId}" : $"steam:{steamId}:{ownerSteamId}");

    public Task<SteamIdentity?> Verify(byte[] ticket, CancellationToken cancellationToken)
    {
        string text = Encoding.UTF8.GetString(ticket);
        if (!text.StartsWith("steam:")) return Task.FromResult<SteamIdentity?>(null);

        string[] parts = text.Split(':');
        long steamId = long.Parse(parts[1]);
        long owner = parts.Length > 2 ? long.Parse(parts[2]) : steamId;
        return Task.FromResult<SteamIdentity?>(new SteamIdentity(steamId, owner, VacBanned: false));
    }
}
