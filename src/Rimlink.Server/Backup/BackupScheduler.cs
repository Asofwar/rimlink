using Microsoft.Extensions.Options;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Backup;

public sealed class BackupOptions
{
    public const string Section = "Backup";

    /// <summary>0 disables scheduled backups.</summary>
    public int IntervalHours { get; set; } = 24;
    public int Keep { get; set; } = 7;
}

/// <summary>Writes &lt;DataDir&gt;/backups/rimlink-&lt;utc timestamp&gt;.tar.zst every <see cref="BackupOptions.IntervalHours"/> and prunes old ones.</summary>
public sealed class BackupScheduler(DataDirectory data, IOptions<BackupOptions> options, ILogger<BackupScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int hours = options.Value.IntervalHours;
        if (hours <= 0)
        {
            log.LogInformation("Scheduled backups are off (Backup:IntervalHours=0)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
                string file = Path.Combine(data.Backups, $"rimlink-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar.zst");
                BackupArchive.Create(data.Root, file);
                Prune(data.Backups, options.Value.Keep);
                log.LogInformation("Backup written: {File}", file);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Scheduled backup failed");
            }
        }
    }

    public static void Prune(string backupsDir, int keep)
    {
        var files = new DirectoryInfo(backupsDir).GetFiles("rimlink-*.tar.zst").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var old in files.Skip(Math.Max(1, keep))) old.Delete();
    }
}
