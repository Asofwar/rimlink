using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Modpack;

public sealed class ModpackService(RimlinkDbContext db, ILogger<ModpackService> log)
{
    public async Task<List<Protocol.V1.ModpackEntry>> Get() =>
        (await db.ModpackEntries.AsNoTracking().OrderBy(e => e.Position).ToListAsync()).Select(ToProto).ToList();

    public async Task Set(IReadOnlyList<Protocol.V1.ModpackEntry> entries, Storage.Entities.Role actorRole, string actorName)
    {
        if (actorRole != Storage.Entities.Role.Admin) throw RpcErrors.PermissionDenied("only an admin can change the modpack");

        var rows = entries
            .Select((e, i) => new Storage.Entities.ModpackEntry
            {
                Position = e.Position == 0 && i > 0 ? i : e.Position,
                PackageId = ModpackComparer.Normalize(e.PackageId),
                Version = e.Version.Trim(),
                WorkshopId = e.WorkshopId == 0 ? null : e.WorkshopId,
                Url = string.IsNullOrWhiteSpace(e.Url) ? null : e.Url.Trim(),
                Kind = e.Kind == Protocol.V1.EntryKind.Allowed ? Storage.Entities.EntryKind.Allowed : Storage.Entities.EntryKind.Required,
                FilesHash = string.IsNullOrWhiteSpace(e.FilesHash) ? null : e.FilesHash.Trim().ToLowerInvariant(),
            })
            .DistinctBy(r => r.PackageId)
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ModpackEntries.ExecuteDeleteAsync();
        db.ModpackEntries.AddRange(rows);
        await db.SaveChangesAsync();
        // Shared settings only work for mods below Rimlink (SetConfigs): drop those the new list moved above it or left out.
        int rimlink = rows.FirstOrDefault(r => r.PackageId == ModContent.RimlinkPackageId)?.Position ?? int.MaxValue;
        var below = rows.Where(r => r.Position > rimlink).Select(r => r.PackageId).ToList();
        int dropped = await db.ModpackConfigs.Where(c => !below.Contains(c.PackageId)).ExecuteDeleteAsync();
        await tx.CommitAsync();
        log.LogInformation("Modpack set by {Actor}: {Count} entries{Dropped}", actorName, rows.Count, dropped > 0 ? $", {dropped} shared settings files dropped" : "");
    }

    public async Task<List<Protocol.V1.ModConfig>> GetConfigs() =>
        (await db.ModpackConfigs.AsNoTracking().OrderBy(c => c.PackageId).ThenBy(c => c.Handle).ToListAsync())
            .Select(c => new Protocol.V1.ModConfig { PackageId = c.PackageId, Handle = c.Handle, Content = ByteString.CopyFrom(c.Content), Hash = c.Hash })
            .ToList();

    /// <summary>
    /// Replaces the shared mod settings (admin only). Each mod must be in the modpack below Rimlink: the game redirects a mod's
    /// settings file from Rimlink's constructor, which runs before the constructors of the mods loaded after it only.
    /// </summary>
    public async Task SetConfigs(IReadOnlyList<Protocol.V1.ModConfig> configs, Storage.Entities.Role actorRole, string actorName)
    {
        if (actorRole != Storage.Entities.Role.Admin) throw RpcErrors.PermissionDenied("only an admin can change the shared mod settings");

        if (configs.Count > Limits.MaxModConfigs) throw Invalid($"at most {Limits.MaxModConfigs} settings files");
        foreach (var config in configs)
        {
            if (config.Content.Length > Limits.MaxModConfigBytes)
                throw Invalid($"{config.PackageId} {config.Handle}: {config.Content.Length} bytes, at most {Limits.MaxModConfigBytes}");
            // Both end up in a file name on every player's disk: a Mod class name and a plain packageId only.
            if (!HandlePattern.IsMatch(config.Handle)) throw Invalid($"{config.PackageId}: '{config.Handle}' is not a Mod class name");
            if (!PackagePattern.IsMatch(ModpackComparer.Normalize(config.PackageId))) throw Invalid($"'{config.PackageId}' is not a packageId");
        }
        long total = configs.Sum(c => (long)c.Content.Length);
        if (total > Limits.MaxModConfigsTotalBytes) throw Invalid($"{total} bytes in all, at most {Limits.MaxModConfigsTotalBytes}");

        var positions = await db.ModpackEntries.AsNoTracking().ToDictionaryAsync(e => e.PackageId, e => e.Position);
        int rimlink = -1;
        if (configs.Count > 0 && !positions.TryGetValue(ModContent.RimlinkPackageId, out rimlink))
            throw Invalid("Rimlink is not in the modpack");
        var rows = new List<Storage.Entities.ModpackConfig>();
        foreach (var config in configs)
        {
            string id = ModpackComparer.Normalize(config.PackageId);
            if (!positions.TryGetValue(id, out int position)) throw Invalid($"{id} is not in the modpack");
            if (position <= rimlink) throw Invalid($"{id} loads before Rimlink: move it below {ModContent.RimlinkPackageId} in the modpack");
            byte[] content = config.Content.ToByteArray();
            rows.Add(new Storage.Entities.ModpackConfig { PackageId = id, Handle = config.Handle.Trim(), Content = content, Hash = ModContent.SettingsHash(content) });
        }
        if (rows.DistinctBy(r => (r.PackageId, r.Handle)).Count() != rows.Count) throw Invalid("a mod class is listed twice");

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ModpackConfigs.ExecuteDeleteAsync();
        db.ModpackConfigs.AddRange(rows);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        log.LogInformation("Modpack configs set by {Actor}: {Count} files ({Bytes} bytes)", actorName, rows.Count, total);
    }

    private static readonly System.Text.RegularExpressions.Regex HandlePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$");
    private static readonly System.Text.RegularExpressions.Regex PackagePattern = new(@"^[a-z0-9._-]+$");

    private static RpcException Invalid(string reason) =>
        RpcErrors.With(StatusCode.InvalidArgument, Protocol.V1.ErrorCode.ModpackConfigsInvalid, reason);

    /// <summary>Replaces the def-name list (table + in-memory set). Admin only.</summary>
    public async Task SetDefs(IReadOnlyCollection<string> defNames, Storage.Entities.Role actorRole, string actorName, ModpackDefs defs)
    {
        if (actorRole < Storage.Entities.Role.Admin) throw Grpc.RpcErrors.PermissionDenied("admin only");
        var clean = defNames.Select(d => d.Trim()).Where(d => d.Length > 0 && d.Length <= 200).Distinct(StringComparer.Ordinal).ToList();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ModpackDefs.ExecuteDeleteAsync();
        db.ModpackDefs.AddRange(clean.Select(d => new Storage.Entities.ModpackDef { DefName = d }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        defs.Replace(clean);
        log.LogInformation("Modpack defs set by {Actor}: {Count} names{Off}", actorName, clean.Count, clean.Count == 0 ? " (blob def check off)" : "");
    }

    /// <summary>Startup: the in-memory set mirrors the table.</summary>
    public static void LoadDefs(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        var defs = scope.ServiceProvider.GetRequiredService<ModpackDefs>();
        defs.Replace(db.ModpackDefs.Select(d => d.DefName).ToList());
    }

    public static Protocol.V1.ModpackEntry ToProto(Storage.Entities.ModpackEntry e) => new()
    {
        Position = e.Position,
        PackageId = e.PackageId,
        Version = e.Version,
        WorkshopId = e.WorkshopId ?? 0,
        Url = e.Url ?? "",
        Kind = e.Kind == Storage.Entities.EntryKind.Allowed ? Protocol.V1.EntryKind.Allowed : Protocol.V1.EntryKind.Required,
        FilesHash = e.FilesHash ?? "",
    };
}
