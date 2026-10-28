using Rimlink.Server.Backup;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Hosting;

/// <summary>Holds &lt;DataDir&gt;/server.lock while the server runs, so the backup tool refuses to restore under a live server.</summary>
public sealed class ServerLock(DataDirectory data, ILogger<ServerLock> log) : IHostedService
{
    private FileStream? handle;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(data.Root, BackupArchive.LockFile);
        try
        {
            handle = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose);
            using var writer = new StreamWriter(handle, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"another server is using '{data.Root}' ({path} is locked)", ex);
        }
        log.LogDebug("Holding {Lock}", path);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        handle?.Dispose();
        handle = null;
        return Task.CompletedTask;
    }
}
