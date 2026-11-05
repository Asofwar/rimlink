using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Backup;

public enum RestoreMode { ExistingInstallationRestore, ImportNewHost, LostAnchorRecovery }
public enum RestoreOutcome { Completed, BackedOut, Maintenance, Refused }
public sealed record RestoreRequest(string RestoreId, RestoreMode Mode, RestoreInspection Inspection, string TargetInstallationId,
    string TargetBoundary, byte[] ConfirmationDigest, string StorageContract, string QualifiedLinuxFileSystem = "");
public sealed record RestoreBackoutRequest(string DataDirectory, string AnchorDirectory, string RestoreId, byte[] IntentDigest,
    string ExpectedPhase, long ExpectedRevision, byte[] ConfirmationDigest);

public sealed record RestoreResult(RestoreOutcome Outcome, string RestoreId, string? InstallationId, string? Boundary,
    string? HeadId, byte[]? HeadDigest, string Reason);

internal enum RestorePhase { Inspected, Fenced, StagedVerified, OldSetRetained, Switching, InstalledVerified, MainCommitted, Completed, BackedOut, BackingOut, MainPrepared }
internal sealed record RestoreBackoutSelection(int Version, string Action, string DataDirectory, string AnchorDirectory, string RestoreId,
    byte[] IntentDigest, string ExpectedPhase, long ExpectedRevision);

internal sealed record RestoreIntent(int Version, RestoreSelection Selection, byte[] SelectionDigest, OutpostBackupIdentity Source,
    byte[] ManifestDigest, OutpostBackupFile[] NewFiles, OutpostBackupFile[] OldFiles, string? FreshFenceId = null,
    RestorePreviousWitness PreviousWitness = RestorePreviousWitness.KnownCommitted);
internal enum RestoreFileAction { Replace, Delete }
internal enum RestoreFileState { Planned, Completed }
internal sealed record RestoreFileStep(string Path, RestoreFileAction Action, RestoreFileState State);
internal sealed record RestoreJournal(int Version, string RestoreId, byte[] IntentDigest, RestorePhase Phase, long Revision,
    bool OldSetUsable, string OldSetReason, RestoreFileStep[]? SwitchSteps = null, byte[]? BackoutConfirmation = null, RestoreFileStep[]? BackoutSteps = null,
    RestoreOldSetKind OldSetKind = RestoreOldSetKind.Unverified);

internal sealed record RestoreSelection(int Version, string RestoreId, RestoreMode Mode, string ArchivePath, byte[] ArchiveDigest,
    byte[] ManifestDigest, string DataDirectory, string AnchorDirectory, string TargetInstallationId, string TargetBoundary,
    bool DataDirectoryExists, bool DatabaseExists, RestoreAnchorState AnchorState, byte[]? ExternalRecord, byte[]? AnchorDigest,
    string DestinationReason, string StorageContract, string QualifiedLinuxFileSystem, RestoreEmptyQualification? EmptyQualification = null, PreviousAnchorObservation? PreviousAnchor = null);

public static class OutpostRestoreJournalCodec
{
    public static byte[] SelectionDigest(RestoreRequest request) => Digest(Freeze(request));

    public static byte[] BackoutDigest(RestoreBackoutRequest request) => SHA256.HashData(Encode(FreezeBackout(request)));

    internal static RestoreBackoutSelection FreezeBackout(RestoreBackoutRequest request)
    {
        foreach (string path in new[] { request.DataDirectory, request.AnchorDirectory })
            if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || Path.GetFullPath(path) != path)
                throw new InvalidDataException("restore-backout-path-invalid");
        if (!Guid.TryParseExact(request.RestoreId, "D", out var id) || id == Guid.Empty || request.IntentDigest.Length != 32
            || request.ExpectedRevision < 0 || !Enum.TryParse<RestorePhase>(request.ExpectedPhase, out var phase)
            || !Enum.IsDefined(phase) || phase.ToString() != request.ExpectedPhase)
            throw new InvalidDataException("restore-backout-selection-invalid");
        return new(1, "Backout", request.DataDirectory, request.AnchorDirectory, request.RestoreId, request.IntentDigest.ToArray(), request.ExpectedPhase, request.ExpectedRevision);
    }

    // Detach mutable digest buffers BEFORE any await; the confirmed selection is never reread from a caller object.
    internal static RestoreSelection Freeze(RestoreRequest request)
    {
        var inspected = request.Inspection; var destination = inspected.Destination;
        var selection = new RestoreSelection(1, request.RestoreId, request.Mode, inspected.ArchivePath, inspected.ArchiveDigest.ToArray(), inspected.ManifestDigest.ToArray(),
            inspected.DataDirectory, inspected.AnchorDirectory, request.TargetInstallationId, request.TargetBoundary, destination.DataDirectoryExists,
            destination.DatabaseExists, destination.AnchorState, destination.External is null ? null : AnchorRecordCodec.Encode(destination.External),
            destination.AnchorDigest?.ToArray(), destination.Reason, request.StorageContract, request.QualifiedLinuxFileSystem,
            destination.EmptyQualification is null ? null : new(destination.EmptyQualification.Version,
                destination.EmptyQualification.KnownEmptyDirectories.ToArray(), destination.EmptyQualification.Digest.ToArray()),
            inspected.PreviousAnchor is null ? null : PreviousAnchorEvidence.Freeze(inspected.PreviousAnchor));
        ValidateSelection(selection);
        return selection;
    }

    internal static void ValidateSelection(RestoreSelection selection)
    {
        foreach (string id in new[] { selection.RestoreId, selection.TargetInstallationId, selection.TargetBoundary })
            if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty) throw new InvalidDataException("restore-selection-id-invalid");
        foreach (string path in new[] { selection.ArchivePath, selection.DataDirectory, selection.AnchorDirectory })
            if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || Path.GetFullPath(path) != path)
                throw new InvalidDataException("restore-selection-path-invalid");
        if (selection.Version != 1 || !Enum.IsDefined(selection.Mode) || !Enum.IsDefined(selection.AnchorState) || selection.ArchiveDigest.Length != 32
            || selection.ManifestDigest.Length != 32 || selection.StorageContract.Length > 64 || selection.QualifiedLinuxFileSystem.Length > 64
            || selection.DestinationReason.Length > 1024 || selection.AnchorDigest is { Length: not 32 })
            throw new InvalidDataException("restore-selection-invalid");
        if (selection.PreviousAnchor is not null) PreviousAnchorEvidence.Validate(selection.PreviousAnchor);
    }

    internal static byte[] Encode<T>(T record)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, OutpostBackupManifest.Json);
        if (bytes.Length > OutpostBackupManifest.MaximumBytes) throw new InvalidDataException("restore-journal-limit");
        return bytes;
    }

    internal static void WriteDurable(string path, byte[] bytes, bool replace = false)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: replace);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("restore-journal-readback-mismatch");
    }

    internal static byte[] Digest(RestoreSelection selection)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(selection, OutpostBackupManifest.Json);
        if (bytes.Length > 65536) throw new InvalidDataException("restore-selection-limit");
        return SHA256.HashData(bytes);
    }
}

public enum RestoreAnchorState { Unavailable, ProvisionedEmpty, Committed, Pending, RestoreInProgress }
public sealed record RestoreEmptyQualification(int Version, string[] KnownEmptyDirectories, byte[] Digest);
public sealed record RestoreDestinationObservation(bool DataDirectoryExists, bool DatabaseExists, RestoreAnchorState AnchorState,
    AnchorRecord? External, byte[]? AnchorDigest, string Reason, RestoreEmptyQualification? EmptyQualification = null);
public sealed record RestoreInspection(string ArchivePath, string DataDirectory, string AnchorDirectory, byte[] ArchiveDigest,
    byte[] ManifestDigest, OutpostBackupManifest Manifest, RestoreDestinationObservation Destination, PreviousAnchorObservation? PreviousAnchor = null);
