using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Backup;

public enum PreviousAnchorReferenceKind { KnownLocation, OperatorDeclaredUnknown }
public sealed record PreviousAnchorReference(PreviousAnchorReferenceKind Kind, string? Location, string? Declaration, string Origin = "OperatorSelection");
public enum PreviousAnchorOutcome { ObservedUnavailable, ObservedCommitted, ObservedPendingOrRestore, Inconclusive, UnobservedOperatorDeclaration }
public enum PreviousAnchorReason { Missing, Uninitialized, Corrupt, ReadDenied, Healthy, OperationInProgress, HotJournal, Unsupported, ChangedDuringRead, LocationUnknown }
public enum PreviousAnchorDigestCoverage { NoBytesAvailable, ExactStoreFiles, ExactAvailableStoreFiles }
public sealed record PreviousAnchorFile(string Name, long Length, byte[] Sha256);
public sealed record PreviousAnchorObservation(PreviousAnchorReference Reference, PreviousAnchorOutcome Outcome, PreviousAnchorReason Reason,
    PreviousAnchorDigestCoverage Coverage, PreviousAnchorFile[] Files, byte[]? Digest);

public sealed partial class OutpostRestoreProcedure
{
    // Reads the selected old store only. SQLite opens a private copy, never the old location:
    // even a read-only SQLite connection may otherwise create WAL/SHM sidecars there.
    public async Task<PreviousAnchorObservation> InspectPreviousAnchorAsync(PreviousAnchorReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reference is null || !Enum.IsDefined(reference.Kind) || string.IsNullOrWhiteSpace(reference.Origin)
            || reference.Origin.Length > 256 || reference.Origin.Any(char.IsControl)) throw new InvalidDataException("previous-anchor-reference-invalid");
        if (reference.Kind == PreviousAnchorReferenceKind.OperatorDeclaredUnknown)
        {
            if (reference.Location is not null || string.IsNullOrWhiteSpace(reference.Declaration) || reference.Declaration.Length > 1024
                || reference.Declaration.Any(char.IsControl)) throw new InvalidDataException("previous-anchor-declaration-invalid");
            return Observation(PreviousAnchorOutcome.UnobservedOperatorDeclaration, PreviousAnchorReason.LocationUnknown);
        }
        if (reference.Declaration is not null || string.IsNullOrWhiteSpace(reference.Location) || reference.Location.Length > 4096
            || reference.Location.Any(char.IsControl)) throw new InvalidDataException("previous-anchor-reference-invalid");
        reference = reference with { Location = Path.GetFullPath(reference.Location) };
        if (reference.Location.StartsWith(@"\\", StringComparison.Ordinal))
            return Observation(PreviousAnchorOutcome.Inconclusive, PreviousAnchorReason.Unsupported);
        string? temporary = null;
        bool locationObserved = false, captured = false;
        try
        {
            RejectLinks(reference.Location);
            if ((File.GetAttributes(reference.Location) & FileAttributes.Directory) == 0)
                return Observation(PreviousAnchorOutcome.Inconclusive, PreviousAnchorReason.Unsupported);
            locationObserved = true;
            temporary = Path.Combine(Path.GetTempPath(), "rimlink-previous-anchor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            var files = await PreviousFilesAsync(reference.Location, temporary, ct);
            captured = true;
            Checkpoint?.Invoke("AfterPreviousAnchorSnapshot");
            if (!files.Any(f => f.Name == "authority.db"))
                return files.Length == 0 ? Observation(PreviousAnchorOutcome.ObservedUnavailable, PreviousAnchorReason.Missing)
                    : Observation(PreviousAnchorOutcome.Inconclusive, PreviousAnchorReason.Unsupported, files);
            PreviousAnchorOutcome outcome;
            PreviousAnchorReason reason;
            if (files.Any(f => f.Name is "authority.db-wal" or "authority.db-shm") || PreviousJournalIsHot(temporary))
            { outcome = PreviousAnchorOutcome.Inconclusive; reason = PreviousAnchorReason.HotJournal; }
            else
            {
                try
                {
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(temporary, "authority.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                    connection.Open();
                    using var schema = connection.CreateCommand();
                    schema.CommandText = "PRAGMA application_id;";
                    long application = Convert.ToInt64(schema.ExecuteScalar());
                    schema.CommandText = "PRAGMA user_version;";
                    long version = Convert.ToInt64(schema.ExecuteScalar());
                    if (application != 1380732737 || version != 1)
                    { outcome = PreviousAnchorOutcome.Inconclusive; reason = PreviousAnchorReason.Unsupported; }
                    else
                    {
                        var envelope = OutpostAnchorStore.ReadCheckedEnvelope(connection);
                        (outcome, reason) = envelope switch
                        {
                            null => (PreviousAnchorOutcome.ObservedUnavailable, PreviousAnchorReason.Uninitialized),
                            KnownAnchorEnvelope { Record: { Pending: null, Restore: null } } => (PreviousAnchorOutcome.ObservedCommitted, PreviousAnchorReason.Healthy),
                            _ => (PreviousAnchorOutcome.ObservedPendingOrRestore, PreviousAnchorReason.OperationInProgress)
                        };
                    }
                }
                catch (InvalidDataException error)
                {
                    bool unsupported = error.Message is "anchor-envelope-version-invalid" or "anchor-envelope-kind-invalid"
                        or "anchor-fresh-phase-or-witness-invalid" or "Unknown or conflicting anchor record phase.";
                    outcome = unsupported ? PreviousAnchorOutcome.Inconclusive : PreviousAnchorOutcome.ObservedUnavailable;
                    reason = unsupported ? PreviousAnchorReason.Unsupported : PreviousAnchorReason.Corrupt;
                }
                catch (SqliteException error)
                {
                    bool corrupt = error.SqliteErrorCode is 11 or 26;
                    outcome = corrupt ? PreviousAnchorOutcome.ObservedUnavailable : PreviousAnchorOutcome.Inconclusive;
                    reason = corrupt ? PreviousAnchorReason.Corrupt : PreviousAnchorReason.Unsupported;
                }
            }
            var after = await PreviousFilesAsync(reference.Location, null, ct);
            if (!JsonSerializer.SerializeToUtf8Bytes(files).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after)))
                return Observation(PreviousAnchorOutcome.Inconclusive, PreviousAnchorReason.ChangedDuringRead);
            return Observation(outcome, reason, files);
        }
        catch (PreviousSourceReadDenied error)
        { return captured ? Observation(PreviousAnchorOutcome.Inconclusive, PreviousAnchorReason.ChangedDuringRead)
            : Observation(PreviousAnchorOutcome.ObservedUnavailable, PreviousAnchorReason.ReadDenied, error.Files); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return locationObserved ? Observation(PreviousAnchorOutcome.Inconclusive, PreviousAnchorReason.ChangedDuringRead)
            : Observation(PreviousAnchorOutcome.ObservedUnavailable, PreviousAnchorReason.Missing); }
        catch (UnauthorizedAccessException)
        { return locationObserved ? Observation(PreviousAnchorOutcome.Inconclusive, PreviousAnchorReason.Unsupported)
            : Observation(PreviousAnchorOutcome.ObservedUnavailable, PreviousAnchorReason.ReadDenied); }
        catch (InvalidDataException)
        { return Observation(PreviousAnchorOutcome.Inconclusive, PreviousAnchorReason.Unsupported); }
        catch (IOException)
        { return Observation(PreviousAnchorOutcome.Inconclusive, PreviousAnchorReason.ChangedDuringRead); }
        finally { if (temporary is not null && Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }

        PreviousAnchorObservation Observation(PreviousAnchorOutcome outcome, PreviousAnchorReason reason, PreviousAnchorFile[]? files = null)
        {
            files ??= [];
            byte[]? digest = PreviousAnchorEvidence.FilesDigest(reference, files);
            return new(reference, outcome, reason, files.Length == 0 ? PreviousAnchorDigestCoverage.NoBytesAvailable
                : reason == PreviousAnchorReason.ReadDenied ? PreviousAnchorDigestCoverage.ExactAvailableStoreFiles
                    : PreviousAnchorDigestCoverage.ExactStoreFiles, files, digest);
        }
    }

    private sealed class PreviousSourceReadDenied(PreviousAnchorFile[] files) : IOException("previous-anchor-source-read-denied")
    { internal PreviousAnchorFile[] Files { get; } = files; }

    private static async Task<PreviousAnchorFile[]> PreviousFilesAsync(string directory, string? copyTo, CancellationToken ct)
    {
        var result = new List<PreviousAnchorFile>();
        long total = 0;
        foreach (string name in new[] { "authority.db", "authority.db-journal", "authority.db-wal", "authority.db-shm" })
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, name);
            FileAttributes attributes;
            try { attributes = Source(() => File.GetAttributes(path)); }
            catch (FileNotFoundException) { continue; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("previous-anchor-file-unsupported");
            Source(() => { RejectLinks(path); return true; });
            await using var input = Source(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true));
            long length = Source(() => input.Length);
            if (length > 512L * 1024 * 1024 - total) throw new InvalidDataException("previous-anchor-files-limit");
            total += length;
            byte[] hash;
            if (copyTo is not null)
            {
                await using var output = new FileStream(Path.Combine(copyTo, name), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true);
                byte[] buffer = new byte[65536];
                long remaining = length;
                while (remaining > 0)
                {
                    int read = await SourceAsync(() => input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct).AsTask());
                    if (read == 0) throw new IOException("previous-anchor-file-changed");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
                if (Source(input.ReadByte) != -1) throw new IOException("previous-anchor-file-changed");
                output.Position = 0;
                hash = await SHA256.HashDataAsync(output, ct);
            }
            else hash = await SourceAsync(() => SHA256.HashDataAsync(input, ct).AsTask());
            if (Source(() => input.Length) != length) throw new IOException("previous-anchor-file-changed");
            result.Add(new(name, length, hash));
        }
        return result.ToArray();

        T Source<T>(Func<T> read)
        {
            try { return read(); }
            catch (UnauthorizedAccessException) { throw new PreviousSourceReadDenied(result.ToArray()); }
        }
        async Task<T> SourceAsync<T>(Func<Task<T>> read)
        {
            try { return await read(); }
            catch (UnauthorizedAccessException) { throw new PreviousSourceReadDenied(result.ToArray()); }
        }
    }

    private static bool PreviousJournalIsHot(string copy)
    {
        string journal = Path.Combine(copy, "authority.db-journal");
        if (!File.Exists(journal) || new FileInfo(journal).Length == 0) return false;
        using var stream = File.OpenRead(journal);
        Span<byte> header = stackalloc byte[28];
        return stream.Read(header) != header.Length || header.IndexOfAnyExcept((byte)0) >= 0;
    }
}

internal static class PreviousAnchorEvidence
{
    internal static PreviousAnchorObservation Freeze(PreviousAnchorObservation value)
    {
        Validate(value);
        return value with { Reference = value.Reference with { }, Files = value.Files.Select(f => f with { Sha256 = f.Sha256.ToArray() }).ToArray(), Digest = value.Digest?.ToArray() };
    }

    internal static byte[]? FilesDigest(PreviousAnchorReference reference, PreviousAnchorFile[] files) => files.Length == 0 ? null
        : SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Version = 1, Subject = "selected-previous-anchor-store-files", Location = reference.Location, Files = files }));

    internal static void Validate(PreviousAnchorObservation value)
    {
        var reference = value.Reference;
        if (reference is null || !Enum.IsDefined(reference.Kind) || string.IsNullOrWhiteSpace(reference.Origin)
            || reference.Origin.Length > 256 || reference.Origin.Any(char.IsControl)
            || !Enum.IsDefined(value.Outcome) || !Enum.IsDefined(value.Reason) || !Enum.IsDefined(value.Coverage)
            || value.Files is null || value.Files.Length > 4) throw new InvalidDataException("previous-anchor-observation-invalid");
        if (reference.Kind == PreviousAnchorReferenceKind.KnownLocation)
        {
            if (reference.Declaration is not null || string.IsNullOrWhiteSpace(reference.Location) || reference.Location.Length > 4096
                || !Path.IsPathFullyQualified(reference.Location) || Path.GetFullPath(reference.Location) != reference.Location
                || reference.Location.Any(char.IsControl)) throw new InvalidDataException("previous-anchor-reference-invalid");
        }
        else if (reference.Location is not null || string.IsNullOrWhiteSpace(reference.Declaration) || reference.Declaration.Length > 1024
            || reference.Declaration.Any(char.IsControl) || value.Outcome != PreviousAnchorOutcome.UnobservedOperatorDeclaration
            || value.Reason != PreviousAnchorReason.LocationUnknown || value.Files.Length != 0)
            throw new InvalidDataException("previous-anchor-declaration-invalid");
        string[] names = ["authority.db", "authority.db-journal", "authority.db-wal", "authority.db-shm"];
        int previous = -1; long total = 0;
        foreach (var file in value.Files)
        {
            int index = file is null ? -1 : Array.IndexOf(names, file.Name);
            if (file is null || index <= previous || file.Length < 0 || file.Length > 512L * 1024 * 1024 - total
                || file.Sha256 is not { Length: 32 }) throw new InvalidDataException("previous-anchor-file-evidence-invalid");
            total += file.Length; previous = index;
        }
        byte[]? expected = FilesDigest(reference, value.Files);
        if (expected is null ? value.Digest is not null || value.Coverage != PreviousAnchorDigestCoverage.NoBytesAvailable
            : value.Digest is not { Length: 32 } || value.Coverage != (value.Reason == PreviousAnchorReason.ReadDenied
                ? PreviousAnchorDigestCoverage.ExactAvailableStoreFiles : PreviousAnchorDigestCoverage.ExactStoreFiles)
                || !CryptographicOperations.FixedTimeEquals(expected, value.Digest))
            throw new InvalidDataException("previous-anchor-digest-coverage-invalid");
    }

    internal static void RequireUnavailable(RestoreSelection selected)
    {
        var previous = selected.PreviousAnchor;
        if (selected.Mode != RestoreMode.LostAnchorRecovery)
        {
            if (previous is not null) throw new InvalidDataException("restore-previous-observation-not-applicable");
            return;
        }
        if (previous is null) throw new InvalidDataException("restore-actual-previous-observation-required");
        Validate(previous);
        if (previous.Reference.Kind != PreviousAnchorReferenceKind.KnownLocation || previous.Outcome != PreviousAnchorOutcome.ObservedUnavailable
            || previous.Reason is not (PreviousAnchorReason.Missing or PreviousAnchorReason.Uninitialized or PreviousAnchorReason.Corrupt or PreviousAnchorReason.ReadDenied))
            throw new InvalidDataException("restore-previous-authority-not-observed-unavailable");
        string old = previous.Reference.Location!;
        if (old.StartsWith(@"\", StringComparison.Ordinal) || Overlap(old, selected.AnchorDirectory) || Overlap(old, selected.DataDirectory))
            throw new InvalidDataException("restore-previous-authority-is-not-new-destination");
        static bool Overlap(string left, string right)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            left = Path.TrimEndingDirectorySeparator(left); right = Path.TrimEndingDirectorySeparator(right);
            return string.Equals(left, right, comparison) || left.StartsWith(right + Path.DirectorySeparatorChar, comparison)
                || right.StartsWith(left + Path.DirectorySeparatorChar, comparison);
        }
    }
}
