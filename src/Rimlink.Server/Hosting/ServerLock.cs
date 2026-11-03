using Rimlink.Server.Backup;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Hosting;

/// <summary>Holds &lt;DataDir&gt;/server.lock while the server runs, so the backup tool refuses to restore under a live server.</summary>
public sealed class ServerLock(DataDirectory data, ILogger<ServerLock> log) : IHostedService, IDisposable
{
    private readonly object gate = new();
    private FileStream? handle;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Acquire();
        return Task.CompletedTask;
    }

    /// <summary>Acquires the process lock before migrations; hosted startup reuses the same handle.</summary>
    public void Acquire()
    {
        lock (gate)
        {
            if (handle is not null) return;
            string path = Path.Combine(data.Root, BackupArchive.LockFile);
            FileStream? acquired = null;
            try
            {
                acquired = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1);
                acquired.SetLength(0);
                using (var writer = new StreamWriter(acquired, leaveOpen: true))
                {
                    writer.Write(Environment.ProcessId);
                    writer.Flush();
                }
                handle = acquired;
            }
            catch (IOException ex)
            {
                acquired?.Dispose();
                throw new InvalidOperationException($"another server is using '{data.Root}' ({path} is locked)", ex);
            }
            catch
            {
                acquired?.Dispose();
                throw;
            }
            log.LogDebug("Holding {Lock}", path);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (gate)
        {
            handle?.Dispose();
            handle = null;
        }
    }
}
