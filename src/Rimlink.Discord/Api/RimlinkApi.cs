using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rimlink.Discord.Api;

/// <summary>
/// The server's admin API (<c>/api/admin/*</c>) as the bot uses it. The <see cref="HttpClient"/> carries the base address,
/// the bearer token and the TLS policy; every write sends the Discord caller as <c>X-Rimlink-Actor</c>.
/// </summary>
public sealed class RimlinkApi(HttpClient http)
{
    public const string ActorHeader = "X-Rimlink-Actor";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public Task<ServerStatusInfo> Status(CancellationToken ct = default) => Get<ServerStatusInfo>("status", ct);

    public Task<PlayerPage> Players(string q, int limit = 25, CancellationToken ct = default) =>
        Get<PlayerPage>($"players?q={Uri.EscapeDataString(q)}&limit={limit}", ct);

    public Task<PlayerCard> Player(long id, CancellationToken ct = default) => Get<PlayerCard>($"players/{id}", ct);

    public Task<List<LotInfo>> Lots(long? sellerId, CancellationToken ct = default) =>
        Get<List<LotInfo>>(sellerId is { } id ? $"economy/lots?playerId={id}" : "economy/lots", ct);

    public Task<List<BanInfo>> Bans(CancellationToken ct = default) => Get<List<BanInfo>>("bans", ct);

    public Task<List<ActionInfo>> Actions(int limit, CancellationToken ct = default) => Get<List<ActionInfo>>($"actions?limit={limit}", ct);

    public Task<List<RaidInfo>> Raids(long? playerId, CancellationToken ct = default) =>
        Get<List<RaidInfo>>(playerId is { } id ? $"raids?playerId={id}" : "raids", ct);

    public Task<RaidStatsInfo> RaidStats(CancellationToken ct = default) => Get<RaidStatsInfo>("raids/stats", ct);

    public Task<List<LiveInfo>> Live(long? playerId, CancellationToken ct = default) =>
        Get<List<LiveInfo>>(playerId is { } id ? $"live?playerId={id}" : "live", ct);

    public Task<EconomyStatsInfo> EconomyStats(CancellationToken ct = default) => Get<EconomyStatsInfo>("economy/stats", ct);

    public Task<List<SaveInfo>> Saves(long playerId, CancellationToken ct = default) => Get<List<SaveInfo>>($"players/{playerId}/saves", ct);

    /// <summary>Returns the server's answer text ("Nova was kicked (spam)" or "Nova is not online").</summary>
    public async Task<string> Kick(string actor, long playerId, string? reason, CancellationToken ct = default) =>
        TextOf(await Send(HttpMethod.Post, $"players/{playerId}/kick", new { reason }, actor, ct));

    public Task Mute(string actor, long playerId, double minutes, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"players/{playerId}/mute", new { minutes }, actor, ct);

    public Task Unmute(string actor, long playerId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"players/{playerId}/mute", null, actor, ct);

    public Task Broadcast(string actor, string text, CancellationToken ct = default) => Send(HttpMethod.Post, "broadcast", new { text }, actor, ct);

    public async Task<BanIssued> Ban(string actor, long playerId, double? days, string reason, CancellationToken ct = default) =>
        Read<BanIssued>(await Send(HttpMethod.Post, "bans", new { playerId, days, reason }, actor, ct));

    public Task Pardon(string actor, long banId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"bans/{banId}", null, actor, ct);

    public async Task<WalletBalance> Wallet(string actor, long playerId, long delta, string? reason, CancellationToken ct = default) =>
        Read<WalletBalance>(await Send(HttpMethod.Post, $"economy/wallet/{playerId}", new { delta, reason }, actor, ct));

    public Task RefundLot(string actor, long id, CancellationToken ct = default) => Send(HttpMethod.Post, $"economy/refund/lot/{id}", null, actor, ct);

    public Task RefundParcel(string actor, long id, CancellationToken ct = default) => Send(HttpMethod.Post, $"economy/refund/parcel/{id}", null, actor, ct);

    public Task FreezeEconomy(string actor, bool frozen, string? reason, CancellationToken ct = default) =>
        Send(HttpMethod.Post, "economy/freeze", new { frozen, reason }, actor, ct);

    public Task CancelRaid(string actor, long id, string? reason, bool returnPawns, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"raids/{id}/cancel", new { reason, returnPawns }, actor, ct);

    public Task FreezeRaids(string actor, bool frozen, string? reason, CancellationToken ct = default) =>
        Send(HttpMethod.Post, "raids/freeze", new { frozen, reason }, actor, ct);

    public Task EndLive(string actor, long id, string? reason, CancellationToken ct = default) => Send(HttpMethod.Post, $"live/{id}/end", new { reason }, actor, ct);

    public Task Peaceful(string actor, long playerId, bool on, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"players/{playerId}/peaceful", new { peaceful = on }, actor, ct);

    public Task Shutdown(string actor, int seconds, string? reason, CancellationToken ct = default) =>
        Send(HttpMethod.Post, "shutdown", new { inSeconds = seconds, reason }, actor, ct);

    public Task Rollback(string actor, long playerId, int version, CancellationToken ct = default) =>
        Send(HttpMethod.Post, string.Create(CultureInfo.InvariantCulture, $"players/{playerId}/saves/{version}/rollback"), null, actor, ct);

    public Task Reset(string actor, long playerId, CancellationToken ct = default) => Send(HttpMethod.Post, $"players/{playerId}/reset", null, actor, ct);

    public Task RecreateWorld(string actor, CancellationToken ct = default) => Send(HttpMethod.Post, "world/recreate", null, actor, ct);

    public async Task<ChatPosted> PostChat(string author, string text, CancellationToken ct = default) =>
        Read<ChatPosted>(await Send(HttpMethod.Post, "chat", new { author, text }, null, ct));

    private async Task<T> Get<T>(string path, CancellationToken ct) => Read<T>(await Send(HttpMethod.Get, path, null, null, ct));

    private async Task<string> Send(HttpMethod method, string path, object? body, string? actor, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Web);
        if (actor is not null) request.Headers.TryAddWithoutValidation(ActorHeader, actor);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new RimlinkApiException(0, ex is TaskCanceledException ? "the server did not answer in time" : ex.Message);
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new RimlinkApiException((int)response.StatusCode, DetailOf(text) ?? response.ReasonPhrase ?? "");
            return text;
        }
    }

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Web) ?? throw new RimlinkApiException(500, "empty answer");

    /// <summary>A plain-text answer, or a JSON string literal (minimal APIs serialize a returned string as text).</summary>
    private static string TextOf(string body) => body.StartsWith('"') ? JsonSerializer.Deserialize<string>(body) ?? "" : body;

    private static string? DetailOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
        }
        catch (JsonException)
        {
            return body.Length > 0 ? body : null;
        }
    }
}
