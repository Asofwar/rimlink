using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Backup;

internal enum RestorePreviousWitness { KnownCommitted, VerifiedEmpty, Unavailable }
internal enum RestoreOldSetKind { Unverified, VerifiedCommitted, VerifiedEmpty, Unavailable }

internal static class OutpostRestoreFence
{
    internal static void Validate(RestoreIntent intent)
    {
        var selected = intent.Selection;
        OutpostRestoreJournalCodec.ValidateSelection(selected);
        PreviousAnchorEvidence.RequireUnavailable(selected);
        if (selected.Mode == RestoreMode.ExistingInstallationRestore)
        {
            var previous = selected.ExternalRecord is null ? null : AnchorRecordCodec.Decode(selected.ExternalRecord);
            if (previous is null || previous.Pending is not null || previous.Restore is not null
                || intent.FreshFenceId is not null || intent.PreviousWitness != RestorePreviousWitness.KnownCommitted
                || selected.AnchorState != RestoreAnchorState.Committed || previous.InstallationId != intent.Source.InstallationId
                || selected.TargetInstallationId != intent.Source.InstallationId)
                throw new InvalidDataException("restore-previous-external-witness-mismatch");
            return;
        }
        if (selected.Mode == RestoreMode.ImportNewHost) ValidateEmpty(selected.DataDirectory, selected.EmptyQualification);
        if (!Guid.TryParseExact(intent.FreshFenceId, "D", out var fence) || fence == Guid.Empty
            || selected.ExternalRecord is not null || selected.AnchorState != RestoreAnchorState.ProvisionedEmpty
            || selected.TargetInstallationId == intent.Source.InstallationId
            || selected.Mode == RestoreMode.ImportNewHost && (intent.PreviousWitness != RestorePreviousWitness.VerifiedEmpty
                || selected.DatabaseExists || intent.OldFiles.Length != 0)
            || selected.Mode == RestoreMode.LostAnchorRecovery && intent.PreviousWitness != RestorePreviousWitness.Unavailable)
            throw new InvalidDataException("restore-fresh-fence-intent-mismatch");
    }

    internal static AnchorEnvelope Marker(RestoreIntent intent, byte[] commitment)
    {
        Validate(intent);
        var selected = intent.Selection;
        if (selected.Mode == RestoreMode.ExistingInstallationRestore)
        {
            var previous = AnchorRecordCodec.Decode(selected.ExternalRecord!);
            return new KnownAnchorEnvelope(previous with { Restore = new(selected.RestoreId, selected.TargetBoundary, ByteString.CopyFrom(commitment)) });
        }
        return new FreshAnchorFence(FreshFenceState.RestoreInProgress,
            selected.Mode == RestoreMode.ImportNewHost ? FreshFenceMode.ImportNewHost : FreshFenceMode.LostAnchorRecovery,
            selected.Mode == RestoreMode.ImportNewHost ? PreviousWitnessKind.AbsentQualifiedEmpty : PreviousWitnessKind.Unavailable,
            intent.FreshFenceId!, selected.RestoreId, selected.TargetInstallationId, intent.Source.WorldId, selected.TargetBoundary, ByteString.CopyFrom(commitment));
    }

    internal static void RequireUnclaimedFreshAnchor(string anchorDir, string? ownRestoreId = null)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(anchorDir))
        {
            string name = Path.GetFileName(path);
            if (name == "restores" && ownRestoreId is not null && Directory.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                var children = Directory.EnumerateFileSystemEntries(path).Take(2).ToArray();
                if (children.Length != 1 || Path.GetFileName(children[0]) != ownRestoreId || !Directory.Exists(children[0])
                    || (File.GetAttributes(children[0]) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("restore-fresh-anchor-unexplained-entry");
                var journalFiles = Directory.EnumerateFileSystemEntries(children[0]).Take(3).ToArray();
                if (journalFiles.Length != 2 || journalFiles.Any(f => Path.GetFileName(f) is not ("intent.json" or "journal.json")
                    || (File.GetAttributes(f) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
                    throw new InvalidDataException("restore-fresh-anchor-unexplained-entry");
                continue;
            }
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || name is not ("authority.db" or "authority.db-journal" or "save-storage.lock")
                || name == "save-storage.lock" && new FileInfo(path).Length != 0)
                throw new InvalidDataException("restore-fresh-anchor-unexplained-entry");
        }
    }

    private sealed record EmptyShape(int Version, string DataDirectory, string[] ControlAllowlist, string[] EmptyDirectories);
    private static readonly string[] Controls = [BackupArchive.LockFile, "save-storage.lock"];
    private static readonly string[] EmptyDirectories = ["archive", "backups", "logs", "saves", "tls"];

    // A positive result covers EVERY root entry, including empty directories; controls may be acquired between the two reads.
    internal static RestoreEmptyQualification? ObserveEmpty(string dataDir, string? ownRestoreId = null)
    {
        if (!Directory.Exists(dataDir)) return null;
        var directories = new List<string>(); int visited = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(dataDir))
        {
            if (++visited > OutpostBackupManifest.MaximumFiles || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            string name = Path.GetFileName(path);
            if (Directory.Exists(path))
            {
                if (name == "restore-work" && ownRestoreId is not null)
                {
                    foreach (string child in Directory.EnumerateFileSystemEntries(path))
                        if (Path.GetFileName(child) != ownRestoreId || !Directory.Exists(child)
                            || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) return null;
                }
                else if (!EmptyDirectories.Contains(name, StringComparer.Ordinal) || Directory.EnumerateFileSystemEntries(path).Any()) return null;
                else directories.Add(name);
            }
            else if (!Controls.Contains(name, StringComparer.Ordinal) || new FileInfo(path).Length != 0) return null;
        }
        string[] empty = directories.Order(StringComparer.Ordinal).ToArray();
        return new(1, empty, EmptyDigest(dataDir, empty));
    }

    private static byte[] EmptyDigest(string dataDir, string[] directories) => SHA256.HashData(
        OutpostRestoreJournalCodec.Encode(new EmptyShape(1, dataDir, Controls, directories)));

    internal static void ValidateEmpty(string dataDir, RestoreEmptyQualification? qualification)
    {
        if (qualification is null || qualification.Version != 1 || qualification.Digest.Length != 32
            || qualification.KnownEmptyDirectories.Length > EmptyDirectories.Length
            || !qualification.KnownEmptyDirectories.SequenceEqual(qualification.KnownEmptyDirectories.Distinct().Order(StringComparer.Ordinal))
            || qualification.KnownEmptyDirectories.Any(d => !EmptyDirectories.Contains(d, StringComparer.Ordinal))
            || !EmptyDigest(dataDir, qualification.KnownEmptyDirectories).AsSpan().SequenceEqual(qualification.Digest))
            throw new InvalidDataException("restore-empty-qualification-invalid");
    }

    internal static void RequireEmptyDestination(string dataDir)
    {
        if (ObserveEmpty(dataDir) is null) throw new InvalidDataException("restore-import-destination-not-qualified-empty");
    }
}
