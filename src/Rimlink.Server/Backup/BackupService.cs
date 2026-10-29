using System.Text.RegularExpressions;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Backup;

public sealed record BackupFile(string Name, long Size, DateTime CreatedAt);

/// <summary>Backup archives under &lt;DataDir&gt;/backups: listing, one creation at a time (manual or scheduled), opening for download.</summary>
public sealed partial class BackupService(DataDirectory data, IOptions<BackupOptions> options, TimeProvider time, ILogger<BackupService> log)
{
    public const string NamePattern = @"^rimlink-\d{8}-\d{6}\.tar\.zst$";

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Newest first.</summary>
    public List<BackupFile> List() =>
        new DirectoryInfo(data.Backups).GetFiles("rimlink-*.tar.zst")
            .Where(f => NameRegex().IsMatch(f.Name))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupFile(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();

    /// <summary>A manual backup: refused with AlreadyExists while another one runs or when the name (one per second) is taken.</summary>
    public async Task<BackupFile> CreateNow()
    {
        using var lease = TryAcquireGate() ?? throw new RpcException(new Status(StatusCode.AlreadyExists, "a backup is already running"));
        return await Create();
    }

    /// <summary>A scheduled backup waits for a running one instead of failing.</summary>
    public async Task<BackupFile> CreateScheduled()
    {
        await gate.WaitAsync();
        try
        {
            return await Create();
        }
        finally
        {
            gate.Release();
        }
    }

    public FileInfo? Open(string name)
    {
        if (!NameRegex().IsMatch(name)) return null;
        var file = new FileInfo(Path.Combine(data.Backups, name));
        return file.Exists ? file : null;
    }

    internal IDisposable? TryAcquireGate() => gate.Wait(0) ? new Lease(gate) : null;

    private async Task<BackupFile> Create()
    {
        string name = $"rimlink-{time.GetUtcNow():yyyyMMdd-HHmmss}.tar.zst";
        string path = Path.Combine(data.Backups, name);
        if (File.Exists(path)) throw new RpcException(new Status(StatusCode.AlreadyExists, "a backup was made this second; try again"));

        await Task.Run(() => BackupArchive.Create(data.Root, path));
        BackupScheduler.Prune(data.Backups, options.Value.Keep, log);
        var info = new FileInfo(path);
        log.LogInformation("Backup written: {File}", path);
        return new BackupFile(name, info.Length, info.LastWriteTimeUtc);
    }

    [GeneratedRegex(NamePattern)]
    private static partial Regex NameRegex();

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }
}
