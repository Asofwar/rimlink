using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Backup;

public sealed partial class OutpostRestoreProcedure
{
    private async Task<RestoreResult> StageAndRetainAsync(RestoreIntent intent, RestoreJournal journal, string journalRoot, OutpostAnchorStore anchor, OutpostAnchorStore.OutpostAnchorLease held, CancellationToken ct)
    {
        var selected = intent.Selection;
        string work = Path.Combine(selected.DataDirectory, "restore-work", selected.RestoreId);
        RejectLinks(work);
        string staged = Path.Combine(work, "staged");
        if (journal.Phase == RestorePhase.Fenced)
        {
            // A pre-verification partial stage is derivable, NOT authority. Unknown paths are never deleted.
            await ValidatePartialInventoryAsync(staged, intent.NewFiles, ct);
            await VerifyInventoryAsync(selected.DataDirectory, intent.OldFiles, ct);
            Directory.CreateDirectory(staged);
            var extracted = await ReadArchiveAsync(selected.ArchivePath, staged, ct, durable: true, fileCopied: path => Checkpoint?.Invoke("AfterStageFile:" + path), resumeCopy: true);
            if (!extracted.ArchiveDigest.AsSpan().SequenceEqual(selected.ArchiveDigest)
                || !extracted.ManifestDigest.AsSpan().SequenceEqual(intent.ManifestDigest)) throw new InvalidDataException("restore-staged-selection-changed");
            await ValidateCutAsync(staged, extracted.Manifest, ct);
            await VerifyInventoryAsync(staged, intent.NewFiles, ct);
            journal = journal with { Phase = RestorePhase.StagedVerified, Revision = checked(journal.Revision + 1) };
            StoreJournal(journalRoot, journal);
            Checkpoint?.Invoke("AfterStagedVerified");
        }
        if (journal.Phase != RestorePhase.StagedVerified) throw new InvalidDataException("restore-staging-phase-invalid");
        await VerifyInventoryAsync(staged, intent.NewFiles, ct);
        await VerifyInventoryAsync(selected.DataDirectory, intent.OldFiles, ct);

        string retained = Path.Combine(work, "retained");
        Directory.CreateDirectory(retained);
        await ValidatePartialInventoryAsync(retained, intent.OldFiles, ct);
        await CopyInventoryAsync(selected.DataDirectory, retained, intent.OldFiles, ct, resumeCopy: true, copied: path => Checkpoint?.Invoke("AfterRetainFile:" + path));
        await VerifyInventoryAsync(retained, intent.OldFiles, ct);
        var usable = intent.PreviousWitness switch
        {
            RestorePreviousWitness.VerifiedEmpty => (Usable: intent.OldFiles.Length == 0, Reason: "verified-empty"),
            RestorePreviousWitness.Unavailable => (Usable: false, Reason: "previous-authority-unavailable"),
            _ => await ValidateRetainedCopyAsync(retained, intent.OldFiles, selected.ExternalRecord, ct)
        };
        var oldKind = intent.PreviousWitness switch
        {
            RestorePreviousWitness.VerifiedEmpty when usable.Usable => RestoreOldSetKind.VerifiedEmpty,
            RestorePreviousWitness.KnownCommitted when usable.Usable => RestoreOldSetKind.VerifiedCommitted,
            _ => RestoreOldSetKind.Unavailable
        };
        await VerifyInventoryAsync(retained, intent.OldFiles, ct);
        // A valid E does not make corrupt old main files usable. They remain retained, never an automatic fallback.
        journal = journal with { Phase = RestorePhase.OldSetRetained, Revision = checked(journal.Revision + 1), OldSetUsable = usable.Usable, OldSetReason = usable.Reason, OldSetKind = oldKind };
        StoreJournal(journalRoot, journal);
        Checkpoint?.Invoke("AfterOldSetRetained");
        return await SwitchAndVerifyAsync(intent, journal, journalRoot, anchor, held, ct);
    }

    private static void StoreJournal(string root, RestoreJournal journal) => OutpostRestoreJournalCodec.WriteDurable(
        Path.Combine(root, "journal.json"), OutpostRestoreJournalCodec.Encode(journal), replace: true);

    private static async Task CopyInventoryAsync(string sourceRoot, string destinationRoot, OutpostBackupFile[] inventory,
        CancellationToken ct, Action<string>? copied = null, bool resumeCopy = false, bool verificationOnly = false)
    {
        foreach (var expected in inventory)
        {
            string source = Path.Combine(sourceRoot, expected.Path.Replace('/', Path.DirectorySeparatorChar));
            string destination = Path.Combine(destinationRoot, expected.Path.Replace('/', Path.DirectorySeparatorChar));
            RejectLinks(source); RejectLinks(destination);
            if (resumeCopy && File.Exists(destination))
            {
                if (await FileMatchesAsync(destination, expected, ct)) continue;
                File.Delete(destination); // Only this explicit intent-owned partial copy; never truncate a possible hard link.
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != expected.Length) throw new InvalidDataException("restore-copy-source-changed");
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct);
            if (!verificationOnly) output.Flush(flushToDisk: true);
            output.Close(); copied?.Invoke(expected.Path);
        }
    }

    private static async Task ValidatePartialInventoryAsync(string root, OutpostBackupFile[] inventory, CancellationToken ct)
    {
        var expected = inventory.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var file in await ReadOldInventoryAsync(root, ct))
            if (!expected.TryGetValue(file.Path, out var declared) || file.Length > declared.Length)
                throw new InvalidDataException("restore-partial-copy-unexpected-file");
    }

    private static async Task VerifyInventoryAsync(string root, OutpostBackupFile[] inventory, CancellationToken ct)
    {
        var actual = await ReadOldInventoryAsync(root, ct);
        if (actual.Length != inventory.Length) throw new InvalidDataException("restore-installed-inventory-mismatch: extra="
            + string.Join(",", actual.Select(f => f.Path).Except(inventory.Select(f => f.Path), StringComparer.Ordinal).Take(8)) + "; missing="
            + string.Join(",", inventory.Select(f => f.Path).Except(actual.Select(f => f.Path), StringComparer.Ordinal).Take(8)));
        var byPath = actual.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var expected in inventory)
            if (!byPath.TryGetValue(expected.Path, out var found) || found.Length != expected.Length || !found.Sha256.AsSpan().SequenceEqual(expected.Sha256))
                throw new InvalidDataException("restore-installed-inventory-mismatch");
    }

    private static async Task<(bool Usable, string Reason)> ValidateRetainedCopyAsync(string retained, OutpostBackupFile[] inventory,
        byte[]? previousExternal, CancellationToken ct)
    {
        // SQLite read-only WAL connections may create sidecars. NEVER run them against the immutable fallback bytes.
        string copy = Path.Combine(Path.GetTempPath(), "rimlink-restore-old-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        try
        {
            await CopyInventoryAsync(retained, copy, inventory, ct);
            await VerifyInventoryAsync(copy, inventory, ct);
            return await ValidateOldSetAsync(copy, previousExternal, ct);
        }
        finally { Directory.Delete(copy, recursive: true); }
    }

    private static async Task<(bool Usable, string Reason)> ValidateOldSetAsync(string retained, byte[]? previousExternal, CancellationToken ct)
    {
        if (previousExternal is null) return (false, "previous-external-witness-unavailable");
        try
        {
            var external = AnchorRecordCodec.Decode(previousExternal);
            var options = new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(retained, BackupArchive.DatabaseFile), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()).Options;
            await using var db = new RimlinkDbContext(options);
            var row = await db.OutpostAuthority.AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null || row.InstallationId != external.InstallationId || row.WorldId != external.WorldId || row.RestoreBoundary != external.RestoreBoundary
                || row.HeadId != external.Committed.Id || !row.HeadDigest.AsSpan().SequenceEqual(external.Committed.Digest.Span) || row.Phase != "Committed")
                return (false, "previous-set-head-mismatch");
            var files = new SaveFiles(DataDirectory.Existing(retained));
            string state = await new OutpostLifecycleEvidence(db, files).IsNoActiveWorldAsync(ct) ? "NoActiveWorld" : "Ready";
            var identity = new OutpostBackupIdentity(row.FormatVersion, row.InstallationId, row.WorldId, row.RestoreBoundary, row.HeadId, row.HeadDigest, row.TransitionId ?? throw new OutpostEvidenceUnavailableException("previous-set-transition-missing"), state);
            var domain = await OutpostAuthority.ReadStoredChainAsync(db, files, row, external.Committed, currentRuntime: false, ct);
            // Ephemeral validation descriptor only. Never serialize or replace the retained E/source manifest.
            var proof = new OutpostBackupManifest(2, identity, DateTime.UtcNow, domain.Digest.ToByteArray(),
                [], (await OutpostBackupCapture.ReadProofsAsync(db, ct)).ToArray(),
                new(1, domain.Format, new(domain.Schema.ProfileId, domain.Schema.Digest.ToByteArray())));
            await ValidateCutAsync(retained, proof, ct);
            return (true, "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or OutpostEvidenceUnavailableException or JsonException)
        { return (false, "previous-set-evidence-invalid: " + error.GetType().Name); }
    }
}
