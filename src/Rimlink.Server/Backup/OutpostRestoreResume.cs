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
    public async Task<RestoreResult> ResumeAsync(string dataDir, string anchorDir, string expectedRestoreId, CancellationToken ct)
    {
        RestoreResult Refused(string reason) => new(RestoreOutcome.Refused, expectedRestoreId, null, null, null, null, reason);
        RestoreResult Held(string reason) => new(RestoreOutcome.Maintenance, expectedRestoreId, null, null, null, null, reason);
        bool fenced = false;
        try
        {
            var loaded = LoadJournal(dataDir, anchorDir, expectedRestoreId);
            var selected = loaded.Intent.Selection;
            OutpostStorageProfile.Validate(selected.DataDirectory, selected.AnchorDirectory, selected.StorageContract, selected.QualifiedLinuxFileSystem);
            using var server = new FileStream(Path.Combine(selected.DataDirectory, BackupArchive.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var anchor = new OutpostAnchorStore(selected.DataDirectory, Options.Create(new OutpostOptions
            { AnchorDirectory = selected.AnchorDirectory, StorageContract = selected.StorageContract, QualifiedLinuxFileSystem = selected.QualifiedLinuxFileSystem }));
            using var authority = await anchor.AcquireAsync(ct);
            using var saves = await new SaveStorageLock(selected.DataDirectory).HoldAsync(ct);
            var actual = LoadJournal(dataDir, anchorDir, expectedRestoreId);
            if (!loaded.IntentBytes.AsSpan().SequenceEqual(actual.IntentBytes)) return Held("restore-journal-changed-under-barrier");
            var journal = actual.Journal;
            var envelope = anchor.ReadEnvelope(authority);
            var external = (envelope as KnownAnchorEnvelope)?.Record;
            fenced = envelope is FreshAnchorFence || external?.Restore is not null;
            var inspected = await InspectAsync(selected.ArchivePath, selected.DataDirectory, selected.AnchorDirectory, ct);
            ValidateSelectedCut(actual.Intent, inspected);
            var previous = selected.ExternalRecord is null ? null : AnchorRecordCodec.Decode(selected.ExternalRecord);
            if (journal.Phase == RestorePhase.Inspected && external == previous && previous is not null)
            {
                await RecheckInitialFactsAsync(actual.Intent, inspected, ct);
                await VerifyInventoryAsync(selected.DataDirectory, actual.Intent.OldFiles, ct);
                fenced = true; // First attempted marker write has an unknown durable outcome on I/O failure.
                anchor.WriteDurable(authority, previous with { Restore = new(selected.RestoreId, selected.TargetBoundary, ByteString.CopyFrom(journal.IntentDigest)) }, CancellationToken.None);
                journal = journal with { Phase = RestorePhase.Fenced, Revision = checked(journal.Revision + 1) };
                StoreJournal(actual.Root, journal);
            }
            if (journal.Phase is RestorePhase.InstalledVerified or RestorePhase.MainPrepared or RestorePhase.MainCommitted or RestorePhase.Completed)
                return await CommitMainAsync(actual.Intent, journal, actual.Root, anchor, authority, ct);
            if (anchor.ReadEnvelope(authority) != OutpostRestoreFence.Marker(actual.Intent, journal.IntentDigest))
                return Held("restore-external-marker-or-witness-mismatch");
            if (journal.Phase == RestorePhase.Inspected)
            {
                journal = journal with { Phase = RestorePhase.Fenced, Revision = checked(journal.Revision + 1) };
                StoreJournal(actual.Root, journal);
            }
            if (journal.Phase is RestorePhase.Fenced or RestorePhase.StagedVerified)
                return await StageAndRetainAsync(actual.Intent, journal, actual.Root, anchor, authority, ct);
            if (journal.Phase is RestorePhase.OldSetRetained or RestorePhase.Switching)
                return await SwitchAndVerifyAsync(actual.Intent, journal, actual.Root, anchor, authority, ct);
            if (journal.Phase == RestorePhase.InstalledVerified)
            {
                return await CommitMainAsync(actual.Intent, journal, actual.Root, anchor, authority, ct);
            }
            return Held("restore-phase-not-completed");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException
            or OutpostEvidenceUnavailableException or JsonException or OperationCanceledException)
        { return fenced ? Held("restore-resume-evidence-refused: " + error.Message) : Refused("restore-resume-refused: " + error.Message); }
    }

    internal sealed record LoadedRestore(string Root, RestoreIntent Intent, RestoreJournal Journal, byte[] IntentBytes);

    internal static LoadedRestore LoadJournal(string dataDir, string anchorDir, string restoreId)
    {
        if (!Guid.TryParseExact(restoreId, "D", out var id) || id == Guid.Empty) throw new InvalidDataException("restore-resume-id-invalid");
        string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        string anchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchorDir));
        string root = Path.Combine(anchor, "restores", restoreId);
        byte[] bytes = ReadJournalBytes(Path.Combine(root, "intent.json"));
        var intent = JsonSerializer.Deserialize<RestoreIntent>(bytes, OutpostBackupManifest.Json) ?? throw new InvalidDataException("restore-intent-missing");
        var journal = JsonSerializer.Deserialize<RestoreJournal>(ReadJournalBytes(Path.Combine(root, "journal.json")), OutpostBackupManifest.Json)
            ?? throw new InvalidDataException("restore-journal-missing");
        var selection = intent.Selection;
        if (intent.Version != 1 || selection.Version != 1 || journal.Version != 1 || selection.RestoreId != restoreId || journal.RestoreId != restoreId
            || selection.DataDirectory != data || selection.AnchorDirectory != anchor || journal.Revision < 0 || !Enum.IsDefined(journal.Phase)
            || journal.OldSetReason.Length > 1024 || journal.IntentDigest.Length != 32 || intent.ManifestDigest.Length != 32
            || !SHA256.HashData(bytes).AsSpan().SequenceEqual(journal.IntentDigest)
            || !OutpostRestoreJournalCodec.Digest(selection).AsSpan().SequenceEqual(intent.SelectionDigest))
            throw new InvalidDataException("restore-journal-commitment-invalid");
        OutpostRestoreJournalCodec.ValidateSelection(selection);
        OutpostRestoreFence.Validate(intent);
        if (!Enum.IsDefined(journal.OldSetKind)) throw new InvalidDataException("restore-old-set-kind-invalid");
        if (selection.ExternalRecord is { } originalExternal) _ = AnchorRecordCodec.Decode(originalExternal);
        foreach (var inventory in new[] { intent.OldFiles, intent.NewFiles })
        {
            if (inventory.Length > OutpostBackupManifest.MaximumFiles) throw new InvalidDataException("restore-inventory-limit");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var file in inventory)
            {
                if (file is null || !paths.Add(file.Path) || file.Length < 0 || file.Length > MaximumArchiveBytes - total || file.Sha256.Length != 32)
                    throw new InvalidDataException("restore-inventory-invalid");
                _ = RestoreFilePath(data, file.Path); total += file.Length;
            }
        }
        if (journal.SwitchSteps is { Length: > OutpostBackupManifest.MaximumFiles }) throw new InvalidDataException("restore-switch-journal-limit");
        return new(root, intent, journal, bytes);
    }

    private static byte[] ReadJournalBytes(string path)
    {
        RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > OutpostBackupManifest.MaximumBytes) throw new InvalidDataException("restore-journal-limit");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("restore-journal-changed-during-read");
        return bytes;
    }

    private static void ValidateSelectedCut(RestoreIntent intent, RestoreInspection inspected)
    {
        if (!intent.Selection.ArchiveDigest.AsSpan().SequenceEqual(inspected.ArchiveDigest)
            || !intent.ManifestDigest.AsSpan().SequenceEqual(inspected.ManifestDigest)
            || !OutpostRestoreJournalCodec.Encode(intent.Source).AsSpan().SequenceEqual(OutpostRestoreJournalCodec.Encode(inspected.Manifest.Authority))
            || !OutpostRestoreJournalCodec.Encode(intent.NewFiles).AsSpan().SequenceEqual(OutpostRestoreJournalCodec.Encode(inspected.Manifest.Files)))
            throw new InvalidDataException("restore-selected-cut-changed");
    }
}
