using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using ZstdSharp;

namespace Rimlink.Server.Backup;

/// <summary>Explicit offline restore. Inspection never publishes a fence or replaces destination bytes.</summary>
public sealed partial class OutpostRestoreProcedure
{
    private const long MaximumArchiveBytes = 128L * 1024 * 1024 * 1024;
    private const long MaximumDatabaseBytes = 512L * 1024 * 1024;
    internal Action<string>? Checkpoint { get; set; }

    public async Task<RestoreResult> ExecuteAsync(RestoreRequest request, CancellationToken ct)
    {
        RestoreResult Refused(string reason) => new(RestoreOutcome.Refused, request.RestoreId, null, null, null, null, reason);
        try
        {
            var selected = OutpostRestoreJournalCodec.Freeze(request);
            byte[] confirmation = request.ConfirmationDigest.ToArray();
            if (confirmation.Length != 32 || !CryptographicOperations.FixedTimeEquals(confirmation, OutpostRestoreJournalCodec.Digest(selected)))
                return Refused("restore-exact-selection-confirmation-required");
            if (!Directory.Exists(selected.DataDirectory) || !Directory.Exists(selected.AnchorDirectory))
                return Refused("restore-destination-or-fence-not-provisioned");
            OutpostStorageProfile.Validate(selected.DataDirectory, selected.AnchorDirectory, selected.StorageContract, selected.QualifiedLinuxFileSystem);
            RejectLinks(selected.ArchivePath); RejectLinks(selected.DataDirectory); RejectLinks(selected.AnchorDirectory);
            // Lifetime process exclusion, NOT a probe that is released before replacement. No truncation of the old lock file.
            using var server = new FileStream(Path.Combine(selected.DataDirectory, BackupArchive.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var anchorStore = new OutpostAnchorStore(selected.DataDirectory, Options.Create(new OutpostOptions
            { AnchorDirectory = selected.AnchorDirectory, StorageContract = selected.StorageContract, QualifiedLinuxFileSystem = selected.QualifiedLinuxFileSystem }));
            using var authority = await anchorStore.AcquireAsync(ct);
            using var saves = await new SaveStorageLock(selected.DataDirectory).HoldAsync(ct);
            var inspected = await InspectAsync(selected.ArchivePath, selected.DataDirectory, selected.AnchorDirectory, ct, selected.PreviousAnchor?.Reference);
            if (!MatchesSelection(selected, inspected)) return Refused("restore-inspected-selection-changed");
            PreviousAnchorEvidence.RequireUnavailable(selected);
            if (selected.TargetBoundary == inspected.Manifest.Authority.RestoreBoundary
                || selected.TargetBoundary == inspected.Destination.External?.RestoreBoundary) return Refused("restore-boundary-must-be-fresh");
            if (selected.Mode == RestoreMode.ExistingInstallationRestore
                && (inspected.Destination.AnchorState != RestoreAnchorState.Committed || inspected.Destination.External is not { } external
                    || external.InstallationId != selected.TargetInstallationId || inspected.Manifest.Authority.InstallationId != selected.TargetInstallationId))
                return Refused("restore-existing-installation-witness-mismatch");
            if (selected.Mode != RestoreMode.ExistingInstallationRestore)
            {
                if (inspected.Destination.AnchorState != RestoreAnchorState.ProvisionedEmpty
                    || inspected.Destination.External is not null || selected.TargetInstallationId == inspected.Manifest.Authority.InstallationId)
                    return Refused("restore-fresh-provisioned-target-and-new-installation-required");
                OutpostRestoreFence.RequireUnclaimedFreshAnchor(selected.AnchorDirectory);
                if (selected.Mode == RestoreMode.ImportNewHost) OutpostRestoreFence.RequireEmptyDestination(selected.DataDirectory);
            }
            return await ExecuteVerifiedAsync(selected, inspected, anchorStore, authority, ct);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OutpostEvidenceUnavailableException)
        { return Refused("restore-preflight-refused: " + error.Message); }
    }

    private static bool MatchesSelection(RestoreSelection selected, RestoreInspection actual)
    {
        var destination = actual.Destination;
        byte[]? external = destination.External is null ? null : AnchorRecordCodec.Encode(destination.External);
        return selected.ArchiveDigest.AsSpan().SequenceEqual(actual.ArchiveDigest) && selected.ManifestDigest.AsSpan().SequenceEqual(actual.ManifestDigest)
            && selected.DataDirectoryExists == destination.DataDirectoryExists && selected.DatabaseExists == destination.DatabaseExists
            && selected.AnchorState == destination.AnchorState && selected.DestinationReason == destination.Reason
            && Same(selected.ExternalRecord, external) && Same(selected.AnchorDigest, destination.AnchorDigest)
            && Same(selected.EmptyQualification is null ? null : OutpostRestoreJournalCodec.Encode(selected.EmptyQualification),
                destination.EmptyQualification is null ? null : OutpostRestoreJournalCodec.Encode(destination.EmptyQualification))
            && Same(selected.PreviousAnchor is null ? null : OutpostRestoreJournalCodec.Encode(selected.PreviousAnchor),
                actual.PreviousAnchor is null ? null : OutpostRestoreJournalCodec.Encode(actual.PreviousAnchor));
        static bool Same(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    }

    private async Task RecheckInitialFactsAsync(RestoreIntent intent, RestoreInspection inspected, CancellationToken ct)
    {
        var selected = intent.Selection;
        var previous = selected.PreviousAnchor is null ? null : await InspectPreviousAnchorAsync(selected.PreviousAnchor.Reference, ct);
        var current = inspected with { Destination = ObserveDestination(selected.DataDirectory, selected.AnchorDirectory), PreviousAnchor = previous };
        if (!MatchesSelection(selected, current)) throw new InvalidDataException("restore-observed-facts-changed-before-first-fence");
        PreviousAnchorEvidence.RequireUnavailable(selected);
        var files = await ReadOldInventoryAsync(selected.DataDirectory, ct);
        if (!OutpostRestoreJournalCodec.Encode(files).AsSpan().SequenceEqual(OutpostRestoreJournalCodec.Encode(intent.OldFiles)))
            throw new InvalidDataException("restore-old-inventory-changed-before-first-fence");
        if (selected.Mode != RestoreMode.ExistingInstallationRestore)
            OutpostRestoreFence.RequireUnclaimedFreshAnchor(selected.AnchorDirectory, selected.RestoreId);
    }

    private async Task<RestoreResult> ExecuteVerifiedAsync(RestoreSelection selected, RestoreInspection inspected, OutpostAnchorStore anchor,
        OutpostAnchorStore.OutpostAnchorLease held, CancellationToken ct)
    {
        // The immutable intent is outside every DB/saves/archive replacement tree.
        string root = Path.Combine(selected.AnchorDirectory, "restores", selected.RestoreId);
        RejectLinks(root);
        if (Directory.Exists(root)) throw new InvalidDataException("restore-journal-exists-use-explicit-resume");
        var oldFiles = await ReadOldInventoryAsync(selected.DataDirectory, ct);
        var intent = new RestoreIntent(1, selected, OutpostRestoreJournalCodec.Digest(selected), inspected.Manifest.Authority,
            inspected.ManifestDigest.ToArray(), inspected.Manifest.Files, oldFiles,
            selected.Mode == RestoreMode.ExistingInstallationRestore ? null : Guid.NewGuid().ToString("D"),
            selected.Mode switch { RestoreMode.ImportNewHost => RestorePreviousWitness.VerifiedEmpty,
                RestoreMode.LostAnchorRecovery => RestorePreviousWitness.Unavailable, _ => RestorePreviousWitness.KnownCommitted });
        OutpostRestoreFence.Validate(intent);
        byte[] intentBytes = OutpostRestoreJournalCodec.Encode(intent);
        byte[] commitment = SHA256.HashData(intentBytes);
        Directory.CreateDirectory(root);
        OutpostRestoreJournalCodec.WriteDurable(Path.Combine(root, "intent.json"), intentBytes);
        var journal = new RestoreJournal(1, selected.RestoreId, commitment, RestorePhase.Inspected, 0, false, "not-verified");
        OutpostRestoreJournalCodec.WriteDurable(Path.Combine(root, "journal.json"), OutpostRestoreJournalCodec.Encode(journal));
        Checkpoint?.Invoke("AfterJournalCreated");
        ct.ThrowIfCancellationRequested();
        await RecheckInitialFactsAsync(intent, inspected, ct);
        var external = inspected.Destination.External;
        var originalEnvelope = external is null ? null : new KnownAnchorEnvelope(external);
        if (anchor.ReadEnvelope(held) != originalEnvelope) throw new InvalidDataException("restore-external-witness-changed");
        try
        {
            // From the FIRST attempted durable marker write, failure is reconciliation, never a business refusal.
            anchor.WriteEnvelopeDurable(held, OutpostRestoreFence.Marker(intent, commitment), CancellationToken.None);
            // BackupDigest commits the FULL immutable intent; archive/manifest digests live inside it.
            journal = journal with { Phase = RestorePhase.Fenced, Revision = 1 };
            StoreJournal(root, journal);
            Checkpoint?.Invoke("AfterExternalFence");
            return await StageAndRetainAsync(intent, journal, root, anchor, held, ct);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or OutpostEvidenceUnavailableException or JsonException or OperationCanceledException)
        { return new(RestoreOutcome.Maintenance, selected.RestoreId, selected.TargetInstallationId, selected.TargetBoundary, null, null, "restore-interrupted: " + error.Message); }
    }

    private static async Task<OutpostBackupFile[]> ReadOldInventoryAsync(string dataDir, CancellationToken ct)
    {
        var paths = new List<string>(); int visited = 0;
        foreach (string name in new[] { BackupArchive.DatabaseFile, BackupArchive.DatabaseFile + "-wal", BackupArchive.DatabaseFile + "-shm", BackupArchive.DatabaseFile + "-journal" })
            if (File.Exists(Path.Combine(dataDir, name))) paths.Add(Path.Combine(dataDir, name));
        foreach (string name in new[] { "saves", "archive" })
        {
            string tree = Path.Combine(dataDir, name);
            if (!Directory.Exists(tree)) continue;
            var directories = new Stack<string>(); directories.Push(tree);
            while (directories.TryPop(out string? directory))
            {
                if (++visited > OutpostBackupManifest.MaximumFiles) throw new InvalidDataException("restore-old-inventory-limit");
                RejectLinks(directory);
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    RejectLinks(child);
                    if (Directory.Exists(child)) directories.Push(child); else paths.Add(child);
                    if (paths.Count > OutpostBackupManifest.MaximumFiles || directories.Count > OutpostBackupManifest.MaximumFiles)
                        throw new InvalidDataException("restore-old-inventory-limit");
                }
            }
        }
        var files = new List<OutpostBackupFile>(); long total = 0;
        foreach (string path in paths.Order(StringComparer.Ordinal))
        {
            RejectLinks(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumArchiveBytes - total) throw new InvalidDataException("restore-old-inventory-limit");
            total += file.Length;
            files.Add(new(Path.GetRelativePath(dataDir, path).Replace(Path.DirectorySeparatorChar, '/'), file.Length, await SHA256.HashDataAsync(file, ct)));
        }
        return files.ToArray();
    }

    public Task<RestoreInspection> InspectAsync(string archivePath, string dataDir, string anchorDir, CancellationToken ct)
        => InspectAsync(archivePath, dataDir, anchorDir, ct, null);

    public async Task<RestoreInspection> InspectAsync(string archivePath, string dataDir, string anchorDir, CancellationToken ct, PreviousAnchorReference? previousAnchor)
    {
        string archive = Path.GetFullPath(archivePath);
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        string anchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchorDir));
        RejectLinks(archive); RejectLinks(destination); RejectLinks(anchor);
        string temporary = Path.Combine(Path.GetTempPath(), "rimlink-restore-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var extracted = await ReadArchiveAsync(archive, temporary, ct);
            await ValidateCutAsync(temporary, extracted.Manifest, ct);
            return new(archive, destination, anchor, extracted.ArchiveDigest, extracted.ManifestDigest,
                extracted.Manifest, ObserveDestination(destination, anchor), previousAnchor is null ? null : await InspectPreviousAnchorAsync(previousAnchor, ct));
        }
        catch (Exception error) when (error is OutpostEvidenceUnavailableException or SqliteException or JsonException)
        { throw new InvalidDataException("restore-inspection-evidence-invalid: " + error.Message, error); }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static async Task<(OutpostBackupManifest Manifest, byte[] ArchiveDigest, byte[] ManifestDigest)> ReadArchiveAsync(
        string archive, string temporary, CancellationToken ct, bool durable = false, Action<string>? fileCopied = null, bool resumeCopy = false)
    {
        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumArchiveBytes) throw new InvalidDataException("restore-archive-size-limit");
        byte[] archiveDigest = await SHA256.HashDataAsync(file, ct); file.Position = 0;
        using var zstd = new DecompressionStream(file, leaveOpen: true);
        using var tar = new TarReader(zstd, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actual = new Dictionary<string, OutpostBackupFile>(StringComparer.Ordinal);
        byte[]? manifestBytes = null;
        long total = 0;
        TarEntry? entry;
        while ((entry = tar.GetNextEntry(copyData: false)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || !names.Add(entry.Name) || names.Count > OutpostBackupManifest.MaximumFiles + 1)
                throw new InvalidDataException("restore-archive-entry-invalid");
            bool manifest = entry.Name == OutpostBackupManifest.FileName;
            ValidateEntryPath(entry.Name, manifest);
            long maximum = manifest ? OutpostBackupManifest.MaximumBytes
                : entry.Name == BackupArchive.DatabaseFile ? MaximumDatabaseBytes : MaximumArchiveBytes;
            if (entry.Length < 0 || entry.Length > maximum || entry.Length > MaximumArchiveBytes - total)
                throw new InvalidDataException("restore-archive-content-limit");
            total += entry.Length;
            string output = Path.Combine(temporary, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            RejectLinks(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (resumeCopy && File.Exists(output)) File.Delete(output); // Derivable partial stage only, verified before publication.
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920]; long copied = 0;
            if (entry.DataStream is { } source)
            {
                int count;
                while ((count = await source.ReadAsync(buffer, ct)) != 0)
                {
                    if (count > entry.Length - copied) throw new InvalidDataException("restore-entry-length-mismatch");
                    await target.WriteAsync(buffer.AsMemory(0, count), ct); hash.AppendData(buffer, 0, count); copied += count;
                }
            }
            if (copied != entry.Length) throw new InvalidDataException("restore-entry-length-mismatch");
            byte[] digest = hash.GetHashAndReset(); target.Flush(flushToDisk: durable);
            if (manifest)
            {
                target.Close(); manifestBytes = await File.ReadAllBytesAsync(output, ct);
            }
            else actual.Add(entry.Name, new(entry.Name, copied, digest));
            target.Close(); fileCopied?.Invoke(entry.Name);
        }
        if (manifestBytes is null || !actual.ContainsKey(BackupArchive.DatabaseFile))
            throw new InvalidDataException("restore-manifest-or-database-missing");
        var decoded = OutpostBackupManifest.Decode(manifestBytes);
        if (decoded.CutUtc.Kind != DateTimeKind.Utc || decoded.DomainDigest.Length != 32
            || decoded.Files.Length > OutpostBackupManifest.MaximumFiles || decoded.Proofs.Length > OutpostBackupManifest.MaximumFiles
            || decoded.Files.Length != actual.Count)
            throw new InvalidDataException("restore-manifest-invalid");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expected in decoded.Files)
        {
            if (expected is null || !listed.Add(expected.Path) || !actual.TryGetValue(expected.Path, out var observed)
                || expected.Sha256.Length != 32 || expected.Length != observed.Length
                || !expected.Sha256.AsSpan().SequenceEqual(observed.Sha256))
                throw new InvalidDataException("restore-file-inventory-mismatch");
        }
        return (decoded, archiveDigest, SHA256.HashData(manifestBytes));
    }

    private static async Task ValidateCutAsync(string temporary, OutpostBackupManifest manifest, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(temporary, BackupArchive.DatabaseFile), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()).Options;
        await using var db = new RimlinkDbContext(options);
        await db.Database.OpenConnectionAsync(ct);
        using (var check = db.Database.GetDbConnection().CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check;";
            if (!Equals(await check.ExecuteScalarAsync(ct), "ok")) throw new InvalidDataException("restore-database-integrity");
        }
        // Admit the actual historical schema before any versioned entity reader. The label cannot select a digest.
        _ = await OutpostDomainSchema.ObserveAsync(db, ct);
        var row = await db.OutpostAuthority.AsNoTracking().SingleOrDefaultAsync(ct);
        var id = manifest.Authority;
        _ = AnchorRecordCodec.Encode(new(id.FormatVersion, id.InstallationId, id.WorldId, id.RestoreBoundary,
            new(id.HeadId, ByteString.CopyFrom(id.HeadDigest))));
        if (row is null || row.Id != 1 || row.Phase != "Committed" || row.FormatVersion != id.FormatVersion
            || row.InstallationId != id.InstallationId || row.WorldId != id.WorldId || row.RestoreBoundary != id.RestoreBoundary
            || row.HeadId != id.HeadId || row.TransitionId != id.TransitionId || !row.HeadDigest.AsSpan().SequenceEqual(id.HeadDigest))
            throw new InvalidDataException("restore-cut-identity-or-domain-mismatch");
        var files = new SaveFiles(DataDirectory.Existing(temporary)); // Descriptor only: this validator also checks an installed cut.
        var domain = await OutpostAuthority.ReadStoredChainAsync(db, files, row, new(id.HeadId, ByteString.CopyFrom(id.HeadDigest)), currentRuntime: false, ct);
        manifest.ValidateDomain(domain);
        await new OutpostSnapshotGuard(db, files).ValidateStoredEvidenceCoreAsync(ct);
        string state = await new OutpostLifecycleEvidence(db, files).IsNoActiveWorldAsync(ct) ? "NoActiveWorld" : "Ready";
        if (state != id.State) throw new InvalidDataException("restore-cut-state-mismatch");
        var required = await OutpostBackupCapture.ReadProofsAsync(db, ct);
        var proofs = new Dictionary<(string Kind, string Id), OutpostBackupProof>();
        foreach (var proof in manifest.Proofs)
            if (proof is null || proof.Sha256.Length != 32 || !proofs.TryAdd((proof.Kind, proof.Id), proof))
                throw new InvalidDataException("restore-proof-inventory-invalid");
        if (proofs.Count != required.Count || required.Any(p => !proofs.TryGetValue((p.Kind, p.Id), out var found)
            || !p.Sha256.AsSpan().SequenceEqual(found.Sha256)))
            throw new InvalidDataException("restore-proof-closure-mismatch");
    }

    private static RestoreDestinationObservation ObserveDestination(string dataDir, string anchorDir)
    {
        bool dataExists = Directory.Exists(dataDir), databaseExists = File.Exists(Path.Combine(dataDir, BackupArchive.DatabaseFile));
        string path = Path.Combine(anchorDir, "authority.db");
        RestoreDestinationObservation Unavailable(string reason) => new(dataExists, databaseExists, RestoreAnchorState.Unavailable, null, null, reason);
        if (!File.Exists(path)) return Unavailable("anchor-missing");
        try
        {
            RejectLinks(path); RejectLinks(path + "-journal");
            if (File.Exists(path + "-wal") || File.Exists(path + "-shm")) return Unavailable("anchor-sidecar-unexpected");
            if (!File.Exists(path + "-journal")) return Unavailable("anchor-journal-missing");
            using (var journal = File.OpenRead(path + "-journal"))
            {
                byte[] header = new byte[28];
                if (journal.Read(header) != header.Length || header.Any(value => value != 0))
                    return Unavailable("anchor-journal-not-proven-cold");
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
            connection.Open();
            AnchorEnvelope? envelope = OutpostAnchorStore.ReadCheckedEnvelope(connection);
            AnchorRecord? record = (envelope as KnownAnchorEnvelope)?.Record;
            return new(dataExists, databaseExists, envelope is null ? RestoreAnchorState.ProvisionedEmpty
                : envelope is FreshAnchorFence || record!.Restore is not null ? RestoreAnchorState.RestoreInProgress
                : record!.Pending is not null ? RestoreAnchorState.Pending : RestoreAnchorState.Committed,
                record, envelope is null ? SHA256.HashData(File.ReadAllBytes(path)) : SHA256.HashData(AnchorEnvelopeCodec.Encode(envelope)), "",
                OutpostRestoreFence.ObserveEmpty(dataDir));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        { return Unavailable("anchor-observation-unavailable: " + error.GetType().Name); }
    }

    private static void ValidateEntryPath(string name, bool manifest)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.Contains('\\') || name.Contains(':')
            || name.Any(char.IsControl) || Path.IsPathRooted(name)) throw new InvalidDataException("restore-path-invalid");
        string[] parts = name.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' '))
            || !manifest && name != BackupArchive.DatabaseFile && (parts.Length < 2 || parts[0] is not ("saves" or "archive")))
            throw new InvalidDataException("restore-path-invalid");
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("restore-path-link");
    }
}
