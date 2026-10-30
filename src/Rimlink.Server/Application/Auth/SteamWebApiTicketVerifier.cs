using System.Text.Json;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Auth;

/// <summary>Checks tickets with ISteamUserAuth/AuthenticateUserTicket (RimWorld app id 294100).</summary>
public sealed class SteamWebApiTicketVerifier(HttpClient http, IOptions<AuthOptions> options, ILogger<SteamWebApiTicketVerifier>? log = null) : ISteamTicketVerifier
{
    public const int RimWorldAppId = 294100;
    private const string Endpoint = "https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/";
    private const string SummariesEndpoint = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/";

    public async Task<SteamIdentity?> Verify(byte[] ticket, CancellationToken cancellationToken)
    {
        string? key = options.Value.SteamWebApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            log?.LogWarning("Steam login attempted but Auth:SteamWebApiKey is not configured");
            return null;
        }

        try
        {
            string url = $"{Endpoint}?key={Uri.EscapeDataString(key)}&appid={RimWorldAppId}&ticket={Convert.ToHexString(ticket)}";
            using var response = await http.GetAsync(url, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));

            if (!json.RootElement.TryGetProperty("response", out var body) || !body.TryGetProperty("params", out var p))
            {
                log?.LogWarning("Steam ticket rejected: {Body}", json.RootElement.ToString());
                return null;
            }
            if (p.GetProperty("result").GetString() != "OK") return null;

            long steamId = long.Parse(p.GetProperty("steamid").GetString()!);
            long owner = p.TryGetProperty("ownersteamid", out var o) ? long.Parse(o.GetString()!) : steamId;
            bool vac = p.TryGetProperty("vacbanned", out var v) && v.GetBoolean();
            return new SteamIdentity(steamId, owner, vac);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            log?.LogWarning(ex, "Steam ticket verification failed");
            return null;
        }
    }

    public async Task<string?> PersonaName(long steamId, CancellationToken cancellationToken)
    {
        string? key = options.Value.SteamWebApiKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            string url = $"{SummariesEndpoint}?key={Uri.EscapeDataString(key)}&steamids={steamId}";
            using var response = await http.GetAsync(url, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var players = json.RootElement.GetProperty("response").GetProperty("players");
            return players.GetArrayLength() > 0 ? players[0].GetProperty("personaname").GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
        {
            log?.LogWarning("Steam name of {SteamId} not available: {Reason}", steamId, ex.GetType().Name);
            return null;
        }
    }
}
