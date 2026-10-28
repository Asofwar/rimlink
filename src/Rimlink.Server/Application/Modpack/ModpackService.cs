using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Modpack;

public sealed class ModpackService(RimlinkDbContext db, ILogger<ModpackService> log)
{
    public async Task<List<Protocol.V1.ModpackEntry>> Get() =>
        (await db.ModpackEntries.AsNoTracking().OrderBy(e => e.Position).ToListAsync()).Select(ToProto).ToList();

    public async Task Set(IReadOnlyList<Protocol.V1.ModpackEntry> entries, Session actor)
    {
        if (actor.Role != Storage.Entities.Role.Admin) throw RpcErrors.PermissionDenied("only an admin can change the modpack");

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
        log.LogInformation("Modpack set by {Player}: {Count} entries", actor.PlayerName, rows.Count);
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
