using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.World;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Saves;

/// <summary>Turns a player's non-current versions into deltas against a base of theirs (save deltas spec §4).</summary>
public sealed class SaveCompactor(RimlinkDbContext db, SaveFiles files, ColonyLocks locks, IOptions<SaveStorageOptions> options, ILogger<SaveCompactor> log)
{
    private sealed record Reference(int Number, byte[] Raw);

    /// <summary>Everyone with a full old version: saves from before an update, what a crash left, colonies back from the archive.</summary>
    public async Task SweepAll(CancellationToken cancellationToken)
    {
        if (!options.Value.CompactOldVersions) return;
        var old = await db.SaveVersions.AsNoTracking().Where(v => !v.IsCurrent).Select(v => new { v.PlayerId, v.Number }).ToListAsync(cancellationToken);
        foreach (long playerId in old.Where(v => File.Exists(files.FullPath(v.PlayerId, v.Number))).Select(v => v.PlayerId).Distinct())
        {
            using var held = await locks.Hold(playerId);
            await files.PerStoreLock.WaitAsync(cancellationToken);
            try
            {
                await Run(playerId);
            }
            finally
            {
                files.PerStoreLock.Release();
            }
        }
    }

    /// <summary>The caller holds <see cref="SaveFiles.PerStoreLock"/>. A version that fails stays as it is and the pass goes on.</summary>
    public async Task Run(long playerId)
    {
        if (!options.Value.CompactOldVersions) return;
        var versions = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == playerId).OrderBy(v => v.Number).ToListAsync();
        var protectedIds = await db.OutpostSnapshotLinks.AsNoTracking().Where(l => l.PlayerId == playerId).Select(l => l.SnapshotId).ToListAsync();
        Reference? reference = null;
        foreach (var version in versions)
        {
            if (version.SnapshotId is not null && protectedIds.Contains(version.SnapshotId)) continue;
            if (!File.Exists(files.FullPath(playerId, version.Number))) continue;
            try
            {
                if (files.RecoverFullMetadata(playerId, version.Number, version.Sha256) is { } rebuilt)
                    await db.SaveVersions.Where(v => v.Id == version.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(v => v.Size, rebuilt.Size).SetProperty(v => v.Sha256, rebuilt.Sha256));
                files.DeleteDeltas(playerId, version.Number);
                if (!version.IsCurrent) reference = Compact(playerId, version.Number, reference);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                log.LogError(ex, "Compacting save v{Version} of player {PlayerId} failed", version.Number, playerId);
            }
        }
        files.PruneBases(playerId);
    }

    /// <returns>The base to use for the next version.</returns>
    private Reference? Compact(long playerId, int number, Reference? reference)
    {
        string full = files.FullPath(playerId, number);
        if (files.ReadRaw(full) is not { } raw) return reference;

        long fullLength = new FileInfo(full).Length;
        reference ??= NewestBase(playerId);
        byte[]? delta = reference is null ? null : SaveDelta.Compress(reference.Raw, raw);
        if (delta is null || delta.Length * (long)SaveDelta.RebaseRatio > fullLength)
        {
            files.WriteAtomically(files.BasePath(playerId, number), File.ReadAllBytes(full));
            reference = new Reference(number, raw);
            delta = SaveDelta.Compress(raw, raw);
        }

        if (!SaveDelta.Decompress(reference!.Raw, delta).AsSpan().SequenceEqual(raw))
            throw new InvalidDataException("the delta does not decode to the version it was made from");
        files.WriteAtomically(files.DeltaPath(playerId, number, reference.Number), delta);
        log.LogInformation("Compacted save v{Version} of player {PlayerId}: {Full} -> {Delta} bytes against base {Base}", number, playerId, fullLength, delta.Length, reference.Number);

        // A download reading it holds the file open on Windows: the next pass finds both forms and finishes the job.
        try { File.Delete(full); }
        catch (IOException ex) { log.LogDebug(ex, "Save v{Version} of player {PlayerId} stays until the next pass", number, playerId); }
        return reference;
    }

    /// <summary>A base that is too large or damaged counts as none: the next version starts a new one.</summary>
    private Reference? NewestBase(long playerId)
    {
        if (files.NewestBase(playerId) is not { } number) return null;
        try
        {
            return files.ReadRaw(files.BasePath(playerId, number)) is { } raw ? new Reference(number, raw) : null;
        }
        catch (InvalidDataException ex)
        {
            log.LogError(ex, "Base {Base} of player {PlayerId} is damaged", number, playerId);
            return null;
        }
    }
}

/// <summary>One pass at startup; from then on every upload compacts the version it replaces.</summary>
public sealed class SaveCompactionService(IServiceScopeFactory scopes, ILogger<SaveCompactionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            if (!Rimlink.Server.Hosting.OutpostDomainHostedServices.CanWrite(scope.ServiceProvider)) return;
            await scope.ServiceProvider.GetRequiredService<SaveCompactor>().SweepAll(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Compacting old saves at startup failed");
        }
    }
}
