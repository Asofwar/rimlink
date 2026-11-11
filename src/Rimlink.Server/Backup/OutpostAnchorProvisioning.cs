using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Backup;

/// <summary>
/// Creates the empty external authority store an administrator needs before the first outpost initialization.
/// A stopped-server step: it never replaces an existing store and creates no logical authority.
/// </summary>
public static class OutpostAnchorProvisioning
{
    public static string Provision(string dataDirectory, OutpostOptions options)
    {
        if (!Directory.Exists(dataDirectory)) throw new InvalidDataException("data directory does not exist");
        FileStream server;
        try
        {
            server = new FileStream(Path.Combine(dataDirectory, BackupArchive.LockFile), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("stop the server before provisioning the authority store", error);
        }
        using (server)
        {
            var store = new OutpostAnchorStore(Path.GetFullPath(dataDirectory), Options.Create(options));
            store.Provision();
            using var read = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            read.Open();
            if (OutpostAnchorStore.ReadCheckedEnvelope(read) is not null)
                throw new InvalidDataException("authority store is not empty after provisioning");
            return store.DatabasePath;
        }
    }
}
