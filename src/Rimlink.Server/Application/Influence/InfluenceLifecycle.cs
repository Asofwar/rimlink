using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using WorldRow = Rimlink.Server.Storage.Entities.World;

namespace Rimlink.Server.Application.Influence;

/// <summary>Called inside the transition's database transaction; notifications follow its commit.</summary>
public static class InfluenceLifecycle
{
    public static void Copy(IInfluenceRecord source, IInfluenceRecord target)
    {
        target.InfluenceEnabled = source.InfluenceEnabled;
        target.InfluenceVersion = checked(source.InfluenceVersion + 1);
        target.InfluenceAllianceId = source.InfluenceAllianceId;
        target.InfluenceRootSurface = source.InfluenceRootSurface;
        target.InfluenceWorldId = source.InfluenceWorldId;
        target.InfluenceSaveGeneration = source.InfluenceSaveGeneration;
        target.InfluenceTile = source.InfluenceTile;
    }

    public static void Revoke(IInfluenceRecord record)
    {
        record.InfluenceEnabled = false;
        record.InfluenceVersion = checked(record.InfluenceVersion + 1);
        record.InfluenceAllianceId = 0;
        record.InfluenceRootSurface = null;
        record.InfluenceWorldId = "";
        record.InfluenceSaveGeneration = 0;
        record.InfluenceTile = -1;
    }

    public static async Task RevokePlayers(RimlinkDbContext db, IEnumerable<long> players, CancellationToken ct = default)
    {
        var ids = players.Distinct().ToArray();
        var homes = await db.WorldObjects.Where(o => ids.Contains(o.OwnerPlayerId)).ToListAsync(ct);
        var archives = await db.ArchivedColonies.Where(o => ids.Contains(o.PlayerId)).ToListAsync(ct);
        foreach (var home in homes) { await db.Entry(home).ReloadAsync(ct); Revoke(home); }
        foreach (var archive in archives) { await db.Entry(archive).ReloadAsync(ct); Revoke(archive); }
    }

    public static async Task<WorldRow?> Advance(RimlinkDbContext db, CancellationToken ct = default)
    {
        // ExecuteUpdate avoids replacing a newer revision with a World tracked before file copying.
        int changed = await db.World.Where(w => w.InfluenceRevision < long.MaxValue).ExecuteUpdateAsync(s => s.SetProperty(w => w.InfluenceRevision, w => w.InfluenceRevision + 1), ct);
        if (changed == 0 && await db.World.AnyAsync(ct)) throw new InvalidOperationException("Influence revision is exhausted");
        return await db.World.AsNoTracking().SingleOrDefaultAsync(ct);
    }
}
