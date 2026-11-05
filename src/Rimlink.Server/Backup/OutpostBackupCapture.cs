using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using ZstdSharp;

namespace Rimlink.Server.Backup;

/// <summary>One A → sorted O → S → Live cut, retained through the SQLite snapshot and every original proof byte.</summary>
public sealed class OutpostBackupCapture(RimlinkDbContext db, DataDirectory data, OutpostAuthority authority,
    OutpostAnchorStore anchor, OutpostAssignmentService assignments, OutpostClock clock, LiveStateLock live)
{
    internal static async Task<List<OutpostBackupProof>> ReadProofsAsync(RimlinkDbContext db, CancellationToken ct)
    {
        var proofs = new List<OutpostBackupProof>();
        foreach (var row in await db.OutpostTransitions.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct))
            proofs.Add(new("transition", row.Id, row.Digest));
        foreach (var row in await db.OutpostSnapshotLinks.AsNoTracking().OrderBy(r => r.WorldId).ThenBy(r => r.PlayerId).ThenBy(r => r.SaveGeneration).ThenBy(r => r.SnapshotId).ToListAsync(ct))
            proofs.Add(new("snapshot", $"{row.WorldId}/{row.PlayerId.ToString(CultureInfo.InvariantCulture)}/{row.SaveGeneration.ToString(CultureInfo.InvariantCulture)}/{row.SnapshotId}", row.RawSha256));
        foreach (var row in await db.OutpostParcelLinks.AsNoTracking().OrderBy(r => r.ParcelId).ToListAsync(ct))
            proofs.Add(new("parcel", row.ParcelId.ToString(CultureInfo.InvariantCulture), row.PayloadDigest));
        foreach (var row in await db.OutpostManifests.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct))
            proofs.Add(new("geographic-manifest", row.Id, row.Digest));
        foreach (var row in await db.OutpostOrdinaryReturns.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct))
            proofs.Add(new("ordinary-return-fence", row.Id, SHA256.HashData(row.Intent)));
        return proofs;
    }

    private void ValidateOutput(string output, bool protectedHistory)
    {
        string full = Path.GetFullPath(output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var protectedRoots = new List<string> { data.SavesRoot, data.Archive };
        if (protectedHistory) protectedRoots.Add(Path.GetDirectoryName(anchor.DatabasePath)!);
        foreach (string root in protectedRoots)
        {
            string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (full.Equals(canonical, comparison) || full.StartsWith(canonical + Path.DirectorySeparatorChar, comparison))
                throw new InvalidDataException("backup-output-overlaps-protected-tree");
        }
        if (full.Equals(data.DatabasePath, comparison) || full.StartsWith(data.DatabasePath + "-", comparison))
            throw new InvalidDataException("backup-output-overlaps-database");
        for (string? parent = Path.GetDirectoryName(full); parent is not null; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("backup-output-path-link");
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("backup-output-already-exists");
    }

    public async Task CreateAsync(string output, CancellationToken ct)
    {
        bool history = await authority.HasHistoryAsync(ct);
        ValidateOutput(output, history);
        if (!history)
        {
            await Task.Run(() => BackupArchive.Create(data.Root, output), ct);
            return;
        }
        long[] owners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        await using var lease = await authority.AcquireAsync(owners, ct);
        using var liveHeld = await live.Hold();
        long[] currentOwners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        if (!owners.SequenceEqual(currentOwners))
            throw new OutpostEvidenceUnavailableException("backup-owner-set-changed");
        var readiness = await authority.CheckHeldAsync(lease, ct);
        if (readiness.State is not (OutpostStartupState.Ready or OutpostStartupState.NoActiveWorld))
            throw new OutpostEvidenceUnavailableException("backup-authority-not-ready: " + readiness.Reason);
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var now = clock.Sample();
        var active = await db.Outposts.AsNoTracking().Where(o => o.State == "Active" && o.WorldId == head.WorldId
            && o.RestoreBoundary == head.RestoreBoundary).OrderBy(o => o.Id).ToListAsync(ct);
        if (active.Count != 0)
        {
            if (active.Any(o => !lease.CoversOwner(o.PlayerId))) throw new OutpostEvidenceUnavailableException("backup-owner-lease-missing");
            await authority.CommitHeldAsync(lease, new(Guid.NewGuid().ToString("D"), head.WorldId, 0, 0, head.RestoreBoundary,
                "Production", "backup", new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.CopyFromUtf8("backup-cut/v1")),
                async (_, token) => { foreach (var row in active) await assignments.SettleAsync(row, now, token); }, ct);
            head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        }
        AnchorRecord observed = anchor.Read(lease.Anchor) ?? throw new OutpostEvidenceUnavailableException("backup-anchor-missing");
        if (observed.Pending is not null || observed.Restore is not null || observed.Committed.Id != head.HeadId
            || !observed.Committed.Digest.Span.SequenceEqual(head.HeadDigest)) throw new OutpostEvidenceUnavailableException("backup-head-changed");
        var identity = new OutpostBackupIdentity(head.FormatVersion, head.InstallationId, head.WorldId, head.RestoreBoundary,
            head.HeadId, head.HeadDigest, head.TransitionId!, readiness.State.ToString());
        var proofs = await ReadProofsAsync(db, ct);
        byte[] domainDigest = (await OutpostDomainDigest.ComputeAsync(db, ct)).ToByteArray();
        string snapshot = Path.Combine(data.Root, "rimlink.db-backup-" + Guid.NewGuid().ToString("N"));
        string temp = Path.GetFullPath(output) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var connection = new SqliteConnection($"Data Source={data.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync(ct);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA synchronous=FULL; VACUUM INTO @path";
                command.Parameters.AddWithValue("@path", snapshot);
                await command.ExecuteNonQueryAsync(ct);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var inventory = new List<OutpostBackupFile>();
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zstd = new CompressionStream(file, level: 3, leaveOpen: true))
                using (var tar = new TarWriter(zstd, TarEntryFormat.Pax, leaveOpen: true))
                {
                    Pack(snapshot, BackupArchive.DatabaseFile);
                    foreach (string root in new[] { data.SavesRoot, data.Archive }) Walk(root);
                    byte[] manifest = new OutpostBackupManifest(1, identity, now.Utc, domainDigest, inventory.ToArray(), proofs.ToArray()).Encode();
                    using var content = new MemoryStream(manifest, writable: false);
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, OutpostBackupManifest.FileName) { DataStream = content });
                    void Walk(string directory)
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("backup-path-link");
                        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("backup-path-link");
                            if (entry is DirectoryInfo child) Walk(child.FullName);
                            else if (!entry.Name.EndsWith(".tmp", StringComparison.Ordinal))
                                Pack(entry.FullName, Path.GetRelativePath(data.Root, entry.FullName).Replace('\\', '/'));
                        }
                    }
                    void Pack(string path, string name)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (inventory.Count >= OutpostBackupManifest.MaximumFiles) throw new InvalidDataException("backup-file-count-limit");
                        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        byte[] hash = SHA256.HashData(source); source.Position = 0;
                        inventory.Add(new(name, source.Length, hash));
                        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = source });
                    }
                }
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, output, overwrite: false);
        }
        finally
        {
            if (File.Exists(snapshot)) File.Delete(snapshot);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
