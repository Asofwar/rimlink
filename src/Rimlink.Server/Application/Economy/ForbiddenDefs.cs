using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Economy;

/// <summary>
/// Defs (thing, stuff or pawn kind) the admin forbade in sends and lots; raids are not affected. Loaded from the
/// forbidden_defs table at startup and replaced through <see cref="ForbiddenDefsService.Set"/>.
/// </summary>
public sealed class ForbiddenDefs
{
    private volatile HashSet<string> names = new(StringComparer.Ordinal);

    public bool Contains(string defName) => names.Contains(defName);

    public IReadOnlyList<string> All => names.Order(StringComparer.Ordinal).ToList();

    public void Replace(IEnumerable<string> defNames) => names = Clean(defNames).ToHashSet(StringComparer.Ordinal);

    public static List<string> Clean(IEnumerable<string> defNames) =>
        defNames.Select(d => d.Trim()).Where(d => d.Length is > 0 and <= 200).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}

/// <summary>Stores the forbidden list (table + in-memory set).</summary>
public sealed class ForbiddenDefsService(RimlinkDbContext db, ForbiddenDefs defs)
{
    public async Task<IReadOnlyList<string>> Set(IEnumerable<string> defNames)
    {
        var clean = ForbiddenDefs.Clean(defNames);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ForbiddenDefs.ExecuteDeleteAsync();
        // ExecuteDelete bypasses the change tracker: rows added by an earlier Set in this scope are still tracked.
        foreach (var tracked in db.ChangeTracker.Entries<ForbiddenDef>().ToList()) tracked.State = EntityState.Detached;
        db.ForbiddenDefs.AddRange(clean.Select(d => new ForbiddenDef { DefName = d }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        defs.Replace(clean);
        return clean;
    }

    /// <summary>Startup: the in-memory set mirrors the table.</summary>
    public static void Load(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        scope.ServiceProvider.GetRequiredService<ForbiddenDefs>().Replace(db.ForbiddenDefs.Select(d => d.DefName).ToList());
    }
}
