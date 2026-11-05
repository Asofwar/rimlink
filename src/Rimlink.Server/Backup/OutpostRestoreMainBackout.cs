using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Backup;

public sealed partial class OutpostRestoreProcedure
{
    private sealed record BackoutInstalled(int Version, byte[] MainDigest, OutpostBackupFile[] Files);

    private async Task<OutpostBackupFile[]> BackoutNewFilesAsync(RestoreIntent intent, RestoreJournal journal, string journalRoot, CancellationToken ct)
    {
        string document = Path.Combine(journalRoot, "backout-installed.json");
        if (journal.Phase is not (RestorePhase.MainPrepared or RestorePhase.MainCommitted) && !(journal.Phase == RestorePhase.BackingOut && File.Exists(document)))
            return intent.NewFiles;
        byte[] mainBytes = ReadJournalBytes(Path.Combine(journalRoot, "main.json"));
        var plan = OutpostRestoreEvidence.Decode<RestoreMainPlan>(mainBytes);
        if (!OutpostRestoreEvidence.Same(plan.Journal, intent) || !plan.JournalDigest.AsSpan().SequenceEqual(journal.IntentDigest))
            throw new InvalidDataException("restore-backout-main-plan-mismatch");
        _ = OutpostRestoreEvidence.Plan(RootTransition(plan));
        BackoutInstalled installed;
        if (File.Exists(document)) installed = OutpostRestoreEvidence.Decode<BackoutInstalled>(ReadJournalBytes(document));
        else
        {
            if (journal.Phase == RestorePhase.BackingOut) throw new InvalidDataException("restore-backout-installed-proof-missing");
            installed = new(1, SHA256.HashData(mainBytes), await ReadOldInventoryAsync(intent.Selection.DataDirectory, ct));
            ValidateMainBackoutInventory(plan, installed);
            // Exact derivable paths and the main plan are durable before any copy; neither is an external witness.
            OutpostRestoreJournalCodec.WriteDurable(document, OutpostRestoreEvidence.Encode(installed));
        }
        if (installed.Version != 1 || installed.MainDigest.Length != 32 || !installed.MainDigest.AsSpan().SequenceEqual(SHA256.HashData(mainBytes)))
            throw new InvalidDataException("restore-backout-installed-plan-mismatch");
        ValidateMainBackoutInventory(plan, installed);
        string frozen = Path.Combine(intent.Selection.DataDirectory, "restore-work", intent.Selection.RestoreId, "backout-installed");
        if (journal.Phase != RestorePhase.BackingOut)
        {
            await VerifyInventoryAsync(intent.Selection.DataDirectory, installed.Files, ct);
            await ValidatePartialInventoryAsync(frozen, installed.Files, ct);
            await CopyInventoryAsync(intent.Selection.DataDirectory, frozen, installed.Files, ct, resumeCopy: true,
                copied: path => Checkpoint?.Invoke("AfterBackoutCurrentFile:" + path));
        }
        await VerifyInventoryAsync(frozen, installed.Files, ct);
        string copy = Path.Combine(Path.GetTempPath(), "rimlink-restore-backout-main-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        try
        {
            await CopyInventoryAsync(frozen, copy, installed.Files, ct);
            await ValidateMainBackoutCopyAsync(copy, plan, ct);
        }
        finally { Directory.Delete(copy, recursive: true); }
        await VerifyInventoryAsync(frozen, installed.Files, ct);
        return installed.Files;
    }

    private static void ValidateMainBackoutInventory(RestoreMainPlan plan, BackoutInstalled installed)
    {
        if (installed.Files.Length > OutpostBackupManifest.MaximumFiles) throw new InvalidDataException("restore-backout-installed-limit");
        var expected = plan.Journal.NewFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var file in plan.Journal.NewFiles)
            expected.Add(plan.SourceDirectory + "/" + file.Path, file with { Path = plan.SourceDirectory + "/" + file.Path });
        byte[] manifest = plan.Cut.Encode();
        string manifestPath = plan.SourceDirectory + "/" + OutpostBackupManifest.FileName;
        expected.Add(manifestPath, new(manifestPath, manifest.Length, SHA256.HashData(manifest)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in installed.Files)
        {
            if (file is null || !seen.Add(file.Path) || file.Length < 0 || file.Length > MaximumArchiveBytes - total || file.Sha256.Length != 32)
                throw new InvalidDataException("restore-backout-installed-inventory-invalid");
            _ = RestoreFilePath(plan.Journal.Selection.DataDirectory, file.Path); total += file.Length;
            if (file.Path is "rimlink.db" or "rimlink.db-wal" or "rimlink.db-shm" or "rimlink.db-journal") continue;
            if (!expected.TryGetValue(file.Path, out var proof) || file.Length != proof.Length || !file.Sha256.AsSpan().SequenceEqual(proof.Sha256))
                throw new InvalidDataException("restore-backout-installed-file-unexplained");
        }
        if (!seen.Contains(BackupArchive.DatabaseFile) || plan.Journal.NewFiles.Any(f => !seen.Contains(f.Path)))
            throw new InvalidDataException("restore-backout-installed-file-missing");
    }

    private static async Task ValidateMainBackoutCopyAsync(string copy, RestoreMainPlan plan, CancellationToken ct)
    {
        await using var database = OpenRestoreDatabase(copy, readOnly: true);
        var authority = await database.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var transitions = await database.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        string rootId = plan.Journal.Selection.RestoreId;
        if (!transitions.TryGetValue(rootId, out var root))
        {
            // MainPrepared may precede its sole DB transaction. Verify the exact original C, not an invented R.
            await ValidateCutAsync(copy, plan.Cut, ct);
            return;
        }
        if (!OutpostRestoreEvidence.Same(OutpostRestoreEvidence.Plan(root), plan)
            || authority.InstallationId != plan.Journal.Selection.TargetInstallationId || authority.WorldId != plan.Cut.Authority.WorldId
            || authority.RestoreBoundary != plan.Journal.Selection.TargetBoundary || authority.TransitionId is null)
            throw new InvalidDataException("restore-backout-main-proof-invalid");
        if (authority.TransitionId != rootId)
        {
            var completion = OutpostRestoreEvidence.CompletionPath(root, plan, authority.TransitionId, transitions);
            if (completion.Count == 0 || completion[0].NextHeadId != authority.HeadId || !completion[0].Digest.AsSpan().SequenceEqual(authority.HeadDigest))
                throw new InvalidDataException("restore-backout-main-descendant-invalid");
        }
        else if (authority.HeadId != root.NextHeadId || !authority.HeadDigest.AsSpan().SequenceEqual(root.Digest))
            throw new InvalidDataException("restore-backout-main-head-invalid");
        var files = new SaveFiles(DataDirectory.Existing(copy));
        string state = await new OutpostLifecycleEvidence(database, files).IsNoActiveWorldAsync(ct) ? "NoActiveWorld" : "Ready";
        var identity = new OutpostBackupIdentity(authority.FormatVersion, authority.InstallationId, authority.WorldId, authority.RestoreBoundary,
            authority.HeadId, authority.HeadDigest, authority.TransitionId, state);
        var proof = new OutpostBackupManifest(1, identity, root.CreatedAt, (await OutpostDomainDigest.ComputeAsync(database, ct)).ToByteArray(),
            [], (await OutpostBackupCapture.ReadProofsAsync(database, ct)).ToArray());
        // Verify C→R→completion and every real source proof on an owned copy. Do not invent an external E or publish anything.
        await ValidateCutAsync(copy, proof, ct);
    }
}
