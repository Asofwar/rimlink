using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Backup;

public sealed partial class OutpostRestoreProcedure
{
    private async Task<RestoreResult> BackoutEmptyAsync(RestoreIntent intent, RestoreJournal journal, string journalRoot,
        RestoreBackoutSelection action, byte[] actionDigest, OutpostAnchorStore anchor, OutpostAnchorStore.OutpostAnchorLease held, CancellationToken ct)
    {
        RestoreResult Refused(string reason) => new(RestoreOutcome.Refused, intent.Selection.RestoreId, null, null, null, null, reason);
        RestoreResult Empty() => new(RestoreOutcome.BackedOut, intent.Selection.RestoreId,
            intent.Selection.TargetInstallationId, intent.Selection.TargetBoundary, null, null, "");
        OutpostRestoreFence.Validate(intent);
        if (intent.Selection.Mode != RestoreMode.ImportNewHost || intent.OldFiles.Length != 0
            || journal.OldSetKind != RestoreOldSetKind.VerifiedEmpty || !journal.OldSetUsable)
            return Refused("restore-backout-verified-empty-required");
        var marker = (FreshAnchorFence)OutpostRestoreFence.Marker(intent, journal.IntentDigest);
        var finished = marker with { State = FreshFenceState.BackedOutEmpty };
        var observed = anchor.ReadEnvelope(held);
        bool resuming = journal.Phase is RestorePhase.BackingOut or RestorePhase.BackedOut;
        if (resuming ? journal.BackoutConfirmation is null || !journal.BackoutConfirmation.AsSpan().SequenceEqual(actionDigest)
            : journal.Phase.ToString() != action.ExpectedPhase || journal.Revision != action.ExpectedRevision)
            return Refused("restore-backout-inspected-phase-changed");
        if (journal.Phase is RestorePhase.BackedOut or RestorePhase.BackingOut && observed == finished)
        {
            if (observed != finished) return Refused("restore-backout-empty-fence-changed");
            RequireEmptyAfterBackout(intent);
            if (journal.Phase != RestorePhase.BackedOut)
                StoreJournal(journalRoot, journal with { Phase = RestorePhase.BackedOut, Revision = checked(journal.Revision + 1) });
            return Empty();
        }
        if (observed != marker) return Refused("restore-backout-marker-missing-or-already-published");
        if (journal.Phase is not (RestorePhase.OldSetRetained or RestorePhase.Switching or RestorePhase.InstalledVerified
            or RestorePhase.MainPrepared or RestorePhase.MainCommitted or RestorePhase.BackingOut))
            return Refused("restore-backout-old-set-not-qualified");
        string retained = Path.Combine(intent.Selection.DataDirectory, "restore-work", intent.Selection.RestoreId, "retained");
        if (!Directory.Exists(retained)) return Refused("restore-backout-empty-retained-set-missing");
        await VerifyInventoryAsync(retained, intent.OldFiles, ct);
        var installed = await BackoutNewFilesAsync(intent, journal, journalRoot, ct);
        RequireDeclaredImportContainers(intent, installed);
        await ValidateBackoutLiveAsync(intent, journal, installed, ct);
        if (journal.Phase != RestorePhase.BackingOut)
        {
            journal = journal with { Phase = RestorePhase.BackingOut, Revision = checked(journal.Revision + 1), BackoutConfirmation = actionDigest, BackoutSteps = [] };
            StoreJournal(journalRoot, journal); Checkpoint?.Invoke("AfterBackoutStarted");
        }
        journal = await RestorePreviousFilesAsync(intent, journal, journalRoot, installed, ct);
        // Only declared directories created by this import may be removed, and only after they are empty.
        foreach (string directory in ImportDirectories(installed).Where(d => !intent.Selection.EmptyQualification!.KnownEmptyDirectories.Contains(d, StringComparer.Ordinal))
            .OrderByDescending(p => p.Length).ThenBy(p => p, StringComparer.Ordinal))
        {
            string full = Path.Combine(intent.Selection.DataDirectory, directory.Replace('/', Path.DirectorySeparatorChar));
            RejectLinks(full);
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
        RequireEmptyAfterBackout(intent);
        if (anchor.ReadEnvelope(held) != marker) return Refused("restore-backout-empty-marker-changed");
        anchor.WriteEnvelopeDurable(held, finished, CancellationToken.None);
        Checkpoint?.Invoke("AfterBackoutPublication");
        StoreJournal(journalRoot, journal with { Phase = RestorePhase.BackedOut, Revision = checked(journal.Revision + 1) });
        Checkpoint?.Invoke("AfterBackedOut");
        return Empty();
    }

    private static HashSet<string> ImportDirectories(OutpostBackupFile[] files)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            string path = file.Path;
            while (path.LastIndexOf('/') is var slash && slash >= 0)
            { path = path[..slash]; directories.Add(path); }
        }
        return directories;
    }

    private static void RequireDeclaredImportContainers(RestoreIntent intent, OutpostBackupFile[] files)
    {
        var directories = ImportDirectories(files);
        RequireImportRoot(intent, directories, allowDatabase: true);
        int visited = 0;
        var pending = new Stack<string>(new[] { "saves", "archive" }.Where(p => Directory.Exists(Path.Combine(intent.Selection.DataDirectory, p))));
        while (pending.TryPop(out string? relative))
        {
            if (++visited > OutpostBackupManifest.MaximumFiles || !directories.Contains(relative))
                throw new InvalidDataException("restore-backout-unknown-import-directory");
            string full = Path.Combine(intent.Selection.DataDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            RejectLinks(full);
            foreach (string path in Directory.EnumerateFileSystemEntries(full))
            {
                RejectLinks(path);
                if (Directory.Exists(path)) pending.Push(Path.GetRelativePath(intent.Selection.DataDirectory, path).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
    }

    private static void RequireEmptyAfterBackout(RestoreIntent intent)
    {
        RequireImportRoot(intent, [], allowDatabase: false);
        var actual = OutpostRestoreFence.ObserveEmpty(intent.Selection.DataDirectory, intent.Selection.RestoreId);
        if (actual is null || !OutpostRestoreEvidence.Same(actual, intent.Selection.EmptyQualification))
            throw new InvalidDataException("restore-backout-empty-qualification-changed");
    }

    private static void RequireImportRoot(RestoreIntent intent, HashSet<string> directories, bool allowDatabase)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(intent.Selection.DataDirectory))
        {
            RejectLinks(path); string name = Path.GetFileName(path);
            if (!Directory.Exists(path))
            {
                if (name is BackupArchive.LockFile or "save-storage.lock")
                { if (new FileInfo(path).Length != 0) throw new InvalidDataException("restore-backout-control-file-changed"); }
                else if (!allowDatabase || name is not ("rimlink.db" or "rimlink.db-wal" or "rimlink.db-shm" or "rimlink.db-journal"))
                    throw new InvalidDataException("restore-backout-import-root-not-empty");
            }
            else if (name == "restore-work")
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                    if (Path.GetFileName(child) != intent.Selection.RestoreId || !Directory.Exists(child)
                        || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("restore-backout-unknown-import-work");
            }
            else if (!directories.Contains(name))
            {
                if (!intent.Selection.EmptyQualification!.KnownEmptyDirectories.Contains(name, StringComparer.Ordinal)
                    || Directory.EnumerateFileSystemEntries(path).Any()) throw new InvalidDataException("restore-backout-import-root-not-empty");
            }
        }
    }
}
