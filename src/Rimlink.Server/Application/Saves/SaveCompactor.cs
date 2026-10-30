using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Saves;

/// <summary>Turns a player's non-current versions into deltas against a base of theirs (save deltas spec §4).</summary>
public sealed class SaveCompactor(RimlinkDbContext db, SaveFiles files, IOptions<SaveStorageOptions> options, ILogger<SaveCompactor> log)
{
    private sealed record Reference(int Number, byte[] Raw);

    /// <summary>The caller holds <see cref="SaveFiles.PerStoreLock"/>. A version that fails stays as it is and the pass goes on.</summary>
    public async Task Run(long playerId)
    {
        if (!options.Value.CompactOldVersions) return;
        var versions = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == playerId).OrderBy(v => v.Number)
            .Select(v => new { v.Number, v.IsCurrent }).ToListAsync();
        Reference? reference = null;
        foreach (var version in versions)
        {
            if (!File.Exists(files.FullPath(playerId, version.Number))) continue;
            try
            {
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
