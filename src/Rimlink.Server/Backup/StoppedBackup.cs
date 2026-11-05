using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Backup;

/// <summary>Offline CLI adapter: no listener, hosted services, migrations or authority initialization.</summary>
public static class StoppedBackup
{
    public static async Task CreateAsync(string dataRoot, string output, OutpostOptions options, CancellationToken ct)
    {
        var data = DataDirectory.Existing(dataRoot);
        if (!File.Exists(data.DatabasePath)) throw new InvalidDataException("backup-database-missing");
        // Another process has different O/Live locks: its lifetime lock must be excluded before opening SQLite.
        using var server = new ServerLock(data, NullLogger<ServerLock>.Instance);
        server.Acquire();
        var connection = new SqliteConnectionStringBuilder { DataSource = data.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection.ToString()).Options);
        var files = new SaveFiles(data);
        var frozen = Options.Create(new OutpostOptions { AnchorDirectory = options.AnchorDirectory,
            StorageContract = options.StorageContract, QualifiedLinuxFileSystem = options.QualifiedLinuxFileSystem });
        var anchor = new OutpostAnchorStore(data, frozen);
        var authority = new OutpostAuthority(db, anchor, files, new ColonyLocks(), new OutpostFeature());
        var clock = new OutpostClock(TimeProvider.System);
        var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        var economy = Options.Create(new EconomyOptions());
        var assignments = new OutpostAssignmentService(db, authority, new OutpostSnapshotGuard(db, files, economy),
            new OutpostManifestService(db, authority, clock, files), new OutpostTileClaims(db, authority),
            new WalletService(db, events), new EconomyGate(economy, new FreezeState(), new PlayerFreeze(db, events)),
            new EconomyGuard(db, economy), clock);
        await new OutpostBackupCapture(db, data, authority, anchor, assignments, clock, new LiveStateLock()).CreateAsync(output, ct);
    }
}
