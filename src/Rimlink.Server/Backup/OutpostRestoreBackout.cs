using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Backup;

public sealed partial class OutpostRestoreProcedure
{
    /// <summary>Read-only action selection. Backout rechecks its exact phase, revision and commitment under all barriers.</summary>
    public RestoreBackoutRequest InspectBackout(string dataDir, string anchorDir, string expectedRestoreId)
    {
        var loaded = LoadJournal(dataDir, anchorDir, expectedRestoreId);
        var selected = loaded.Intent.Selection;
        return new(selected.DataDirectory, selected.AnchorDirectory, selected.RestoreId, loaded.Journal.IntentDigest.ToArray(),
            loaded.Journal.Phase.ToString(), loaded.Journal.Revision, []);
    }

    public async Task<RestoreResult> BackoutAsync(RestoreBackoutRequest request, CancellationToken ct)
    {
        bool fenced = false;
        RestoreResult Refused(string reason) => new(RestoreOutcome.Refused, request.RestoreId, null, null, null, null, reason);
        try
        {
            var action = OutpostRestoreJournalCodec.FreezeBackout(request);
            byte[] confirmed = request.ConfirmationDigest.ToArray();
            byte[] actionDigest = SHA256.HashData(OutpostRestoreJournalCodec.Encode(action));
            if (confirmed.Length != 32 || !CryptographicOperations.FixedTimeEquals(confirmed, actionDigest))
                return Refused("restore-backout-exact-confirmation-required");
            var loaded = LoadJournal(action.DataDirectory, action.AnchorDirectory, action.RestoreId);
            if (!loaded.Journal.IntentDigest.AsSpan().SequenceEqual(action.IntentDigest)) return Refused("restore-backout-intent-mismatch");
            var selected = loaded.Intent.Selection;
            OutpostStorageProfile.Validate(selected.DataDirectory, selected.AnchorDirectory, selected.StorageContract, selected.QualifiedLinuxFileSystem);
            using var server = new FileStream(Path.Combine(selected.DataDirectory, BackupArchive.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var anchor = new OutpostAnchorStore(selected.DataDirectory, Options.Create(new OutpostOptions
            { AnchorDirectory = selected.AnchorDirectory, StorageContract = selected.StorageContract, QualifiedLinuxFileSystem = selected.QualifiedLinuxFileSystem }));
            using var authority = await anchor.AcquireAsync(ct);
            using var saves = await new SaveStorageLock(selected.DataDirectory).HoldAsync(ct);
            var actual = LoadJournal(action.DataDirectory, action.AnchorDirectory, action.RestoreId);
            if (!loaded.IntentBytes.AsSpan().SequenceEqual(actual.IntentBytes)) return Refused("restore-backout-intent-changed-under-barrier");
            var journal = actual.Journal;
            var observed = anchor.ReadEnvelope(authority);
            var external = (observed as KnownAnchorEnvelope)?.Record;
            fenced = observed is FreshAnchorFence || external?.Restore is not null;
            if (actual.Intent.PreviousWitness == RestorePreviousWitness.VerifiedEmpty)
                return await BackoutEmptyAsync(actual.Intent, journal, actual.Root, action, actionDigest, anchor, authority, ct);
            if (actual.Intent.PreviousWitness == RestorePreviousWitness.Unavailable)
                return Refused("restore-backout-previous-authority-unavailable");
            var previous = selected.ExternalRecord is null ? null : AnchorRecordCodec.Decode(selected.ExternalRecord);
            if (previous is null || previous.Pending is not null || previous.Restore is not null)
                return Refused("restore-backout-verified-previous-witness-required");
            bool resuming = journal.Phase is RestorePhase.BackingOut or RestorePhase.BackedOut;
            if (resuming ? journal.BackoutConfirmation is null || !journal.BackoutConfirmation.AsSpan().SequenceEqual(actionDigest)
                : journal.Phase.ToString() != action.ExpectedPhase || journal.Revision != action.ExpectedRevision)
                return Refused("restore-backout-inspected-phase-changed");
            if (journal.Phase == RestorePhase.BackedOut)
            {
                if (external != previous) return Refused("restore-backout-completed-head-changed");
                await ValidateBackedOutAsync(actual.Intent, ct);
                return BackedOutResult(actual.Intent, previous);
            }
            if (external == previous && journal.Phase == RestorePhase.BackingOut)
            {
                // A failed journal write after external backout publication is completion-only, never another fence.
                await ValidateBackedOutAsync(actual.Intent, ct);
                StoreJournal(actual.Root, journal with { Phase = RestorePhase.BackedOut, Revision = checked(journal.Revision + 1) });
                return BackedOutResult(actual.Intent, previous);
            }
            var marker = ((KnownAnchorEnvelope)await BackoutMainMarkerAsync(actual.Intent, journal, actual.Root, anchor, authority, ct)).Record;
            if (external != marker) return Refused("restore-backout-marker-missing-or-already-published");
            if (journal.Phase is not (RestorePhase.OldSetRetained or RestorePhase.Switching or RestorePhase.InstalledVerified or RestorePhase.MainPrepared or RestorePhase.MainCommitted or RestorePhase.BackingOut)
                || !journal.OldSetUsable) return Refused("restore-backout-old-set-not-qualified");
            string retained = Path.Combine(selected.DataDirectory, "restore-work", selected.RestoreId, "retained");
            await VerifyInventoryAsync(retained, actual.Intent.OldFiles, ct);
            if (!(await ValidateRetainedCopyAsync(retained, actual.Intent.OldFiles, selected.ExternalRecord, ct)).Usable)
                return Refused("restore-backout-retained-proof-invalid");
            var nextFiles = await BackoutNewFilesAsync(actual.Intent, journal, actual.Root, ct);
            await ValidateBackoutLiveAsync(actual.Intent, journal, nextFiles, ct);
            if (journal.Phase != RestorePhase.BackingOut)
            {
                journal = journal with { Phase = RestorePhase.BackingOut, Revision = checked(journal.Revision + 1), BackoutConfirmation = actionDigest, BackoutSteps = [] };
                StoreJournal(actual.Root, journal);
                Checkpoint?.Invoke("AfterBackoutStarted");
            }
            journal = await RestorePreviousFilesAsync(actual.Intent, journal, actual.Root, nextFiles, ct);
            await VerifyInventoryAsync(selected.DataDirectory, actual.Intent.OldFiles, ct);
            await ValidateBackedOutAsync(actual.Intent, ct);
            if (await BackoutMainMarkerAsync(actual.Intent, journal, actual.Root, anchor, authority, ct) != new KnownAnchorEnvelope(marker))
                return Refused("restore-backout-marker-changed");
            anchor.WriteDurable(authority, previous, CancellationToken.None);
            Checkpoint?.Invoke("AfterBackoutPublication");
            StoreJournal(actual.Root, journal with { Phase = RestorePhase.BackedOut, Revision = checked(journal.Revision + 1) });
            Checkpoint?.Invoke("AfterBackedOut");
            return BackedOutResult(actual.Intent, previous);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException
            or OutpostEvidenceUnavailableException or JsonException or OperationCanceledException)
        {
            string reason = "restore-backout-evidence-refused: " + error.Message[..Math.Min(error.Message.Length, 512)];
            return new(fenced ? RestoreOutcome.Maintenance : RestoreOutcome.Refused, request.RestoreId, null, null, null, null, reason);
        }
    }

    private static RestoreResult BackedOutResult(RestoreIntent intent, AnchorRecord previous) => new(RestoreOutcome.BackedOut,
        intent.Selection.RestoreId, previous.InstallationId, previous.RestoreBoundary, previous.Committed.Id, previous.Committed.Digest.ToByteArray(), "");

    private static async Task ValidateBackedOutAsync(RestoreIntent intent, CancellationToken ct)
    {
        // Ordinary harmless reads may create WAL/SHM after completion. Revalidate the actual full E graph on a COPY.
        var actual = await ReadOldInventoryAsync(intent.Selection.DataDirectory, ct);
        if (!(await ValidateRetainedCopyAsync(intent.Selection.DataDirectory, actual, intent.Selection.ExternalRecord, ct)).Usable)
            throw new InvalidDataException("restore-backout-current-E-proof-invalid");
    }

    private static async Task ValidateBackoutLiveAsync(RestoreIntent intent, RestoreJournal journal, OutpostBackupFile[] nextFiles, CancellationToken ct)
    {
        var old = intent.OldFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var next = nextFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        string[] paths = old.Keys.Union(next.Keys, StringComparer.Ordinal)
            .OrderBy(p => p == BackupArchive.DatabaseFile ? 1 : 0).ThenBy(p => p, StringComparer.Ordinal).ToArray();
        var steps = journal.BackoutSteps ?? [];
        if (steps.Length > paths.Length) throw new InvalidDataException("restore-backout-step-count-invalid");
        foreach (var file in await ReadOldInventoryAsync(intent.Selection.DataDirectory, ct))
            if (!old.ContainsKey(file.Path) && !next.ContainsKey(file.Path)) throw new InvalidDataException("restore-backout-unknown-live-file");
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            var action = old.ContainsKey(path) ? RestoreFileAction.Replace : RestoreFileAction.Delete;
            if (i < steps.Length && (steps[i].Path != path || steps[i].Action != action || !Enum.IsDefined(steps[i].State)))
                throw new InvalidDataException("restore-backout-step-invalid");
            string target = RestoreFilePath(intent.Selection.DataDirectory, path);
            bool matchesOld = await FileMatchesAsync(target, old.GetValueOrDefault(path), ct);
            if (i < steps.Length && steps[i].State == RestoreFileState.Completed && !matchesOld)
                throw new InvalidDataException("restore-backout-completed-file-changed");
            if (!matchesOld && !await FileMatchesAsync(target, next.GetValueOrDefault(path), ct))
                throw new InvalidDataException("restore-backout-live-effect-ambiguous");
        }
    }

    private async Task<RestoreJournal> RestorePreviousFilesAsync(RestoreIntent intent, RestoreJournal journal, string journalRoot, OutpostBackupFile[] nextFiles, CancellationToken ct)
    {
        string work = Path.Combine(intent.Selection.DataDirectory, "restore-work", intent.Selection.RestoreId);
        var old = intent.OldFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        string[] paths = old.Keys.Union(nextFiles.Select(f => f.Path), StringComparer.Ordinal)
            .OrderBy(p => p == BackupArchive.DatabaseFile ? 1 : 0).ThenBy(p => p, StringComparer.Ordinal).ToArray();
        var steps = (journal.BackoutSteps ?? []).ToList();
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            var wanted = old.GetValueOrDefault(path);
            string target = RestoreFilePath(intent.Selection.DataDirectory, path);
            if (i < steps.Count && steps[i].State == RestoreFileState.Completed) continue;
            if (i == steps.Count)
            {
                steps.Add(new(path, wanted is null ? RestoreFileAction.Delete : RestoreFileAction.Replace, RestoreFileState.Planned));
                journal = journal with { BackoutSteps = steps.ToArray(), Revision = checked(journal.Revision + 1) };
                StoreJournal(journalRoot, journal);
                Checkpoint?.Invoke("AfterBackoutPlanned:" + path);
            }
            if (!await FileMatchesAsync(target, wanted, ct))
            {
                if (wanted is null) File.Delete(target);
                else
                {
                    string source = RestoreFilePath(Path.Combine(work, "retained"), path);
                    string scratch = RestoreFilePath(Path.Combine(work, "backout-copy"), path);
                    Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
                    if (File.Exists(scratch)) File.Delete(scratch); // Verified intent-owned partial copy, not a live file.
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { await input.CopyToAsync(output, ct); output.Flush(flushToDisk: true); }
                    if (!await FileMatchesAsync(scratch, wanted, ct)) throw new InvalidDataException("restore-backout-copy-mismatch");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(scratch, target, overwrite: true);
                }
            }
            Checkpoint?.Invoke("AfterBackoutEffect:" + path);
            if (!await FileMatchesAsync(target, wanted, ct)) throw new InvalidDataException("restore-backout-file-mismatch");
            steps[i] = steps[i] with { State = RestoreFileState.Completed };
            journal = journal with { BackoutSteps = steps.ToArray(), Revision = checked(journal.Revision + 1) };
            StoreJournal(journalRoot, journal);
            Checkpoint?.Invoke("AfterBackoutCompleted:" + path);
        }
        return journal;
    }
}
