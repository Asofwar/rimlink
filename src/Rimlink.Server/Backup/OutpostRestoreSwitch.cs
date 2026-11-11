using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Backup;

public sealed partial class OutpostRestoreProcedure
{
    private async Task<RestoreResult> SwitchAndVerifyAsync(RestoreIntent intent, RestoreJournal journal, string journalRoot, OutpostAnchorStore anchor, OutpostAnchorStore.OutpostAnchorLease held, CancellationToken ct)
    {
        string work = Path.Combine(intent.Selection.DataDirectory, "restore-work", intent.Selection.RestoreId);
        string staged = Path.Combine(work, "staged");
        await VerifyInventoryAsync(staged, intent.NewFiles, ct);
        await VerifyInventoryAsync(Path.Combine(work, "retained"), intent.OldFiles, ct);
        if (journal.Phase == RestorePhase.OldSetRetained)
        {
            await VerifyInventoryAsync(intent.Selection.DataDirectory, intent.OldFiles, ct);
            journal = journal with { Phase = RestorePhase.Switching, Revision = checked(journal.Revision + 1), SwitchSteps = [] };
            StoreJournal(journalRoot, journal);
        }
        if (journal.Phase != RestorePhase.Switching) throw new InvalidDataException("restore-switch-phase-invalid");
        var steps = (journal.SwitchSteps ?? []).ToList();
        var next = intent.NewFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var previous = intent.OldFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        // The DB is replaced last; old WAL/SHM/journal are separate journalled deletions, never applied to the selected DB.
        string[] paths = previous.Keys.Union(next.Keys, StringComparer.Ordinal)
            .OrderBy(p => p == BackupArchive.DatabaseFile ? 1 : 0).ThenBy(p => p, StringComparer.Ordinal).ToArray();
        if (steps.Count > paths.Length) throw new InvalidDataException("restore-switch-journal-invalid");
        for (int index = 0; index < paths.Length; index++)
        {
            string path = paths[index];
            var action = next.ContainsKey(path) ? RestoreFileAction.Replace : RestoreFileAction.Delete;
            string target = RestoreFilePath(intent.Selection.DataDirectory, path);
            bool planned = index < steps.Count;
            if (planned && (steps[index].Path != path || steps[index].Action != action || !Enum.IsDefined(steps[index].State)))
                throw new InvalidDataException("restore-switch-journal-invalid");
            bool alreadyNew = await FileMatchesAsync(target, next.GetValueOrDefault(path), ct);
            if (planned && steps[index].State == RestoreFileState.Completed)
            {
                if (!alreadyNew) throw new InvalidDataException("restore-completed-file-changed");
                continue;
            }
            if (!planned)
            {
                if (!await FileMatchesAsync(target, previous.GetValueOrDefault(path), ct))
                    throw new InvalidDataException("restore-live-file-changed-before-plan");
                steps.Add(new(path, action, RestoreFileState.Planned));
                journal = journal with { Revision = checked(journal.Revision + 1), SwitchSteps = steps.ToArray() };
                StoreJournal(journalRoot, journal);
                Checkpoint?.Invoke("AfterSwitchPlanned:" + path);
            }
            else if (!alreadyNew && !await FileMatchesAsync(target, previous.GetValueOrDefault(path), ct))
                throw new InvalidDataException("restore-file-effect-ambiguous");
            if (!alreadyNew)
            {
                if (action == RestoreFileAction.Replace)
                {
                    string source = RestoreFilePath(staged, path);
                    if (!await FileMatchesAsync(source, next[path], ct)) throw new InvalidDataException("restore-staged-file-changed");
                    string scratch = RestoreFilePath(Path.Combine(work, "install"), path);
                    Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
                    if (File.Exists(scratch)) File.Delete(scratch); // Owned, derivable incomplete copy; no live bytes removed here.
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { await input.CopyToAsync(output, ct); output.Flush(flushToDisk: true); }
                    if (!await FileMatchesAsync(scratch, next[path], ct)) throw new InvalidDataException("restore-install-copy-mismatch");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(scratch, target, overwrite: true);
                }
                else File.Delete(target);
            }
            Checkpoint?.Invoke("AfterSwitchEffect:" + path);
            if (!await FileMatchesAsync(target, next.GetValueOrDefault(path), ct)) throw new InvalidDataException("restore-file-effect-mismatch");
            steps[index] = steps[index] with { State = RestoreFileState.Completed };
            journal = journal with { Revision = checked(journal.Revision + 1), SwitchSteps = steps.ToArray() };
            StoreJournal(journalRoot, journal);
            Checkpoint?.Invoke("AfterSwitchCompleted:" + path);
        }
        await VerifyInventoryAsync(intent.Selection.DataDirectory, intent.NewFiles, ct);
        byte[] manifestBytes = await File.ReadAllBytesAsync(Path.Combine(staged, OutpostBackupManifest.FileName), ct);
        if (!SHA256.HashData(manifestBytes).AsSpan().SequenceEqual(intent.ManifestDigest)) throw new InvalidDataException("restore-staged-manifest-changed");
        var manifest = OutpostBackupManifest.Decode(manifestBytes);
        await ValidateCutAsync(intent.Selection.DataDirectory, manifest, ct, selectedAcceptance: intent.Selection.Isolated?.Source);
        journal = journal with { Phase = RestorePhase.InstalledVerified, Revision = checked(journal.Revision + 1) };
        StoreJournal(journalRoot, journal);
        Checkpoint?.Invoke("AfterInstalledVerified");
        return await CommitMainAsync(intent, journal, journalRoot, anchor, held, ct);
    }

    private static string RestoreFilePath(string root, string path)
    {
        if (path is not ("rimlink.db-wal" or "rimlink.db-shm" or "rimlink.db-journal")) ValidateEntryPath(path, manifest: false);
        string target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        RejectLinks(target);
        return target;
    }

    internal static async Task<bool> FileMatchesAsync(string path, OutpostBackupFile? expected, CancellationToken ct)
    {
        RejectLinks(path);
        if (Directory.Exists(path)) return false;
        if (!File.Exists(path)) return expected is null;
        if (expected is null) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return file.Length == expected.Length && (await SHA256.HashDataAsync(file, ct)).AsSpan().SequenceEqual(expected.Sha256);
    }
}
