using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Backup;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Admin;

public sealed record PublicModpackEntry(int Position, string PackageId, string Version, ulong WorkshopId, string Url, string Kind);
public sealed record PublicPlayer(string Name, string FactionName);
public sealed record PublicWorld(int Settlements, int TradesToday, int RaidsResolvedToday, int LiveActive, bool EconomyFrozen, bool RaidsFrozen);
public sealed record PublicStatus(
    string ServerName, string Description, string Version, int ProtocolVersion, int PlayersOnline, int MaxPlayers, bool WorldExists,
    bool PasswordRequired, bool SteamLogin, bool PasswordLogin, string CertificateFingerprint,
    List<PublicModpackEntry> Modpack, List<PublicPlayer>? Players, PublicWorld World, string Address = "");

/// <summary>Server status for the admin panel and integrations, the redacted config view, and the public page's data (spec §4).</summary>
public sealed class ServerStatus(
    RimlinkDbContext db,
    SessionRegistry sessions,
    AdminOperations ops,
    ModpackService modpack,
    ServerCertificate certificate,
    BackupService backups,
    TimeProvider time,
    IOptions<ServerOptions> server,
    IOptions<TlsOptions> tls,
    IOptions<AuthOptions> auth,
    IOptions<BackupOptions> backup,
    IOptions<EconomyOptions> economy,
    IOptions<RaidOptions> raids,
    IOptions<LiveOptions> live,
    IOptions<PanelOptions> panel,
    IOptions<AdminOptions> admin,
    IOptions<HygieneOptions> hygiene,
    ILogger<ServerStatus> log)
{
    public static readonly DateTime StartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>Keys replaced by "set"/"" in <see cref="Config"/>; every new secret option is added here.</summary>
    internal static readonly (string Section, string Key)[] Secrets = [("server", "serverPassword"), ("auth", "steamWebApiKey"), ("admin", "apiToken")];

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<object> Admin()
    {
        var economyStats = await Part("economy", () => ops.EconomyStats(Actor.Panel));
        var raidStats = await Part("raids", () => ops.RaidStats(Actor.Panel));
        return new
        {
            version = ServerVersion.Current,
            protocolVersion = Protocol.ProtocolVersion.Current,
            startedAt = StartedAt,
            uptimeSeconds = (long)(time.GetUtcNow().UtcDateTime - StartedAt).TotalSeconds,
            playersOnline = sessions.All.Count,
            maxPlayers = server.Value.MaxPlayers,
            playersTotal = await Part("players", () => db.Players.CountAsync()),
            worldExists = await db.World.AnyAsync(),
            worldCreatedAt = await db.World.Select(w => (DateTime?)w.CreatedAt).FirstOrDefaultAsync(),
            settlements = await Part("settlements", () => db.WorldObjects.CountAsync(o => o.Type == WorldObjectTypes.Settlement)),
            economy = economyStats is null ? null : new { frozen = economyStats.Frozen, reason = economyStats.FreezeReason },
            raids = raidStats is null ? null : new { frozen = raidStats.Frozen, reason = raidStats.FreezeReason, active = raidStats.Queued + raidStats.Announced + raidStats.Landed },
            liveActive = await Part("live", LiveActive),
            saves = await Part("saves", async () => new { count = await db.SaveVersions.CountAsync(), totalBytes = await db.SaveVersions.SumAsync(v => (long?)v.Size) ?? 0 }),
            backups = await Part("backups", () =>
            {
                var list = backups.List();
                return Task.FromResult(new { count = list.Count, lastAt = list.Count > 0 ? list[0].CreatedAt : (DateTime?)null });
            }),
            certificateFingerprint = certificate.Fingerprint,
            certificateSelfSigned = certificate.SelfSigned,
        };
    }

    public JsonObject Config()
    {
        var config = new JsonObject
        {
            ["server"] = JsonSerializer.SerializeToNode(server.Value, Web),
            ["tls"] = JsonSerializer.SerializeToNode(tls.Value, Web),
            ["auth"] = JsonSerializer.SerializeToNode(auth.Value, Web),
            ["backup"] = JsonSerializer.SerializeToNode(backup.Value, Web),
            ["economy"] = JsonSerializer.SerializeToNode(economy.Value, Web),
            ["raids"] = JsonSerializer.SerializeToNode(raids.Value, Web),
            ["live"] = JsonSerializer.SerializeToNode(live.Value, Web),
            ["panel"] = JsonSerializer.SerializeToNode(panel.Value, Web),
            ["hygiene"] = JsonSerializer.SerializeToNode(hygiene.Value, Web),
            // Built by hand, never serialised: Tokens[].Token must not leave the server.
            ["admin"] = new JsonObject
            {
                ["apiToken"] = admin.Value.ApiToken,
                ["tokens"] = new JsonArray(admin.Value.Tokens.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name,
                    ["scopes"] = new JsonArray(t.Scopes.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                }).ToArray()),
            },
        };
        foreach (var (section, key) in Secrets)
            if (config[section] is JsonObject obj && obj.ContainsKey(key))
                obj[key] = string.IsNullOrEmpty(obj[key]?.GetValue<string>()) ? "" : "set";
        return config;
    }

    public async Task<PublicStatus> Public()
    {
        var economyStats = await ops.EconomyStats(Actor.Panel);
        var raidStats = await ops.RaidStats(Actor.Panel);
        var onlineIds = sessions.All.Select(s => s.PlayerId).ToList();
        List<PublicPlayer>? players = null;
        if (panel.Value.PublicPlayerNames)
            players = await db.Players.AsNoTracking().Where(p => onlineIds.Contains(p.Id)).OrderBy(p => p.Name)
                .Select(p => new PublicPlayer(p.Name, p.FactionName)).ToListAsync();

        return new PublicStatus(
            panel.Value.DisplayName,
            panel.Value.DescriptionText,
            ServerVersion.Current,
            Protocol.ProtocolVersion.Current,
            onlineIds.Count,
            server.Value.MaxPlayers,
            await db.World.AnyAsync(),
            !string.IsNullOrEmpty(server.Value.ServerPassword),
            !string.IsNullOrEmpty(auth.Value.SteamWebApiKey),
            auth.Value.AllowPassword,
            certificate.Fingerprint,
            (await modpack.Get()).OrderBy(e => e.Position)
                .Select(e => new PublicModpackEntry(e.Position, e.PackageId, e.Version, e.WorkshopId, e.Url, e.Kind.ToString().ToLowerInvariant())).ToList(),
            players,
            new PublicWorld(
                await db.WorldObjects.CountAsync(o => o.Type == WorldObjectTypes.Settlement),
                economyStats.TradesToday,
                raidStats.ResolvedToday,
                await LiveActive(),
                economyStats.Frozen,
                raidStats.Frozen));
    }

    private Task<int> LiveActive() => db.LiveSessions.CountAsync(s => !LiveSessionService.FinalStates.Contains(s.State));

    /// <summary>One part of the status; a failure leaves it null and is logged instead of failing the whole answer.</summary>
    private async Task<T?> Part<T>(string name, Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Status part {Part} failed", name);
            return default;
        }
    }
}

/// <summary>The public status rebuilt at most every 15 seconds, so an open endpoint cannot load the database.</summary>
public sealed class PublicStatusCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim gate = new(1, 1);
    private PublicStatus? value;
    private DateTimeOffset builtAt;

    public async Task<PublicStatus> Get(Func<Task<PublicStatus>> build)
    {
        if (Fresh() is { } cached) return cached;
        await gate.WaitAsync();
        try
        {
            if (Fresh() is { } again) return again;
            value = await build();
            builtAt = time.GetUtcNow();
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    private PublicStatus? Fresh() => value is not null && time.GetUtcNow() - builtAt < Lifetime ? value : null;
}
