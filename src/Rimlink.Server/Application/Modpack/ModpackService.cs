using Microsoft.EntityFrameworkCore;
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
            })
            .DistinctBy(r => r.PackageId)
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ModpackEntries.ExecuteDeleteAsync();
        db.ModpackEntries.AddRange(rows);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        log.LogInformation("Modpack set by {Actor}: {Count} entries", actorName, rows.Count);
    }

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
    };
}
