using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Saves;

public sealed class SaveRejected(string reason) : Exception(reason);

/// <summary>Versioned save storage: blobs under &lt;data&gt;/saves/&lt;playerId&gt;/&lt;n&gt;.rws.zst, metadata in save_versions.</summary>
public sealed class SaveStore(RimlinkDbContext db, DataDirectory data, EventBus events, ILogger<SaveStore> log)
{
    private static readonly SemaphoreSlim PerStoreLock = new(1, 1);

    public async Task<SaveVersion> Store(long playerId, SaveHeader header, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken cancellationToken)
    {
        if (header.TotalSize <= 0 || header.TotalSize > Limits.MaxSaveBytes)
            throw new SaveRejected($"save size {header.TotalSize} is outside 1..{Limits.MaxSaveBytes} bytes");
        if (header.Sha256.Length != 32) throw new SaveRejected("header has no SHA-256");

        var world = await db.World.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? throw RpcErrors.Failed(ErrorCode.WorldNotCreated, "the world has not been created yet");
        var player = await db.Players.AsNoTracking().FirstAsync(p => p.Id == playerId, cancellationToken);

        string dir = data.Saves(playerId);
        string temp = Path.Combine(dir, $"upload-{Guid.NewGuid():N}.tmp");
        try
        {
            long written = 0;
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await foreach (var chunk in chunks.WithCancellation(cancellationToken))
                {
                    written += chunk.Length;
                    if (written > header.TotalSize) throw new SaveRejected("more data than the header announced");
                    await file.WriteAsync(chunk, cancellationToken);
                    sha.AppendData(chunk.Span);
                }
                if (written != header.TotalSize) throw new SaveRejected($"expected {header.TotalSize} bytes, got {written}");
                if (!sha.GetHashAndReset().AsSpan().SequenceEqual(header.Sha256.Span)) throw new SaveRejected("SHA-256 does not match");
            }

            if (SaveValidator.Validate(temp, world.Seed, player.FactionName) is { } reason) throw new SaveRejected(reason);

            await PerStoreLock.WaitAsync(cancellationToken);
            try
            {
                int number = (await db.SaveVersions.Where(v => v.PlayerId == playerId).MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;
                string final = Path.Combine(dir, $"{number}.rws.zst");
                File.Move(temp, final, overwrite: false);

                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
                await db.SaveVersions.Where(v => v.PlayerId == playerId && v.IsCurrent).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false), cancellationToken);
                var version = new SaveVersion
                {
                    PlayerId = playerId,
                    Number = number,
                    GameTick = header.GameTick,
                    Size = written,
                    Sha256 = header.Sha256.ToByteArray(),
                    CreatedAt = DateTime.UtcNow,
                    IsCurrent = true,
                };
                db.SaveVersions.Add(version);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                await ApplyRetention(playerId);
                log.LogInformation("Stored save v{Version} for player {PlayerId}: {Size} bytes, tick {Tick}", number, playerId, written, header.GameTick);
                return version;
            }
            finally
            {
                PerStoreLock.Release();
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<(SaveVersion Version, Stream Content)?> OpenCurrent(long playerId)
    {
        var version = await db.SaveVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PlayerId == playerId && v.IsCurrent);
        if (version is null) return null;
        var stream = new FileStream(PathOf(version), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return (version, stream);
    }

    public Task<List<SaveVersion>> List(long playerId) =>
        db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == playerId).OrderBy(v => v.Number).ToListAsync();

    public Task<bool> HasSave(long playerId) => db.SaveVersions.AnyAsync(v => v.PlayerId == playerId && v.IsCurrent);

    public async Task<bool> Rollback(long playerId, int number)
    {
        var target = await db.SaveVersions.FirstOrDefaultAsync(v => v.PlayerId == playerId && v.Number == number);
        if (target is null || !File.Exists(PathOf(target))) return false;

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SaveVersions.Where(v => v.PlayerId == playerId && v.IsCurrent).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false));
        target.IsCurrent = true;
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        events.SendTo(playerId, new ServerEvent { SaveRolledBack = new SaveRolledBack { Version = number } });
        log.LogInformation("Save of player {PlayerId} rolled back to v{Version}", playerId, number);
        return true;
    }

    /// <summary>Moves every save file of the player into the archive directory and forgets the versions.</summary>
    public async Task Archive(long playerId, string archiveDir)
    {
        Directory.CreateDirectory(archiveDir);
        foreach (var version in await db.SaveVersions.Where(v => v.PlayerId == playerId).ToListAsync())
        {
            string path = PathOf(version);
            if (File.Exists(path)) File.Move(path, Path.Combine(archiveDir, Path.GetFileName(path)), overwrite: true);
            db.SaveVersions.Remove(version);
        }
        await db.SaveChangesAsync();
    }

    public static SaveInfo ToInfo(SaveVersion v) => new()
    {
        Version = v.Number,
        Size = v.Size,
        CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(v.CreatedAt, DateTimeKind.Utc)),
        IsCurrent = v.IsCurrent,
        GameTick = v.GameTick,
    };

    private string PathOf(SaveVersion v) => Path.Combine(data.Saves(v.PlayerId), $"{v.Number}.rws.zst");

    private async Task ApplyRetention(long playerId)
    {
        var all = await db.SaveVersions.Where(v => v.PlayerId == playerId).ToListAsync();
        foreach (var old in SaveRetention.Select(all, DateTime.UtcNow))
        {
            string path = PathOf(old);
            if (File.Exists(path)) File.Delete(path);
            db.SaveVersions.Remove(old);
        }
        await db.SaveChangesAsync();
    }
}
