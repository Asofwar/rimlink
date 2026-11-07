namespace Rimlink.Server.Application.Outposts;

/// <summary>
/// Bounded deployment checks for the explicitly accepted SQLite/OS contract, not a hardware durability proof.
/// The operator must exclude synchronized folders and dishonest flush/locking stacks; drive metadata cannot
/// establish that premise or reliably detect arbitrary cloud/sync software. Validation does not provision storage.
/// </summary>
public static class OutpostStorageProfile
{
    internal static string CanonicalExistingDirectory(string path) => ExistingDirectory(path);
    public static void Validate(string dataDirectory, string anchorDirectory, string contract, string qualifiedLinuxFileSystem) =>
        _ = Observe(dataDirectory, anchorDirectory, contract, qualifiedLinuxFileSystem);

    internal sealed record Observation(string DataDirectory, string AnchorDirectory,
        OutpostInitializeFileSystemFacts DataFileSystem, OutpostInitializeFileSystemFacts AnchorFileSystem);

    internal static Observation Observe(string dataDirectory, string anchorDirectory, string contract, string qualifiedLinuxFileSystem)
    {
        if (!string.Equals(contract, "sqlite-local-v1", StringComparison.Ordinal))
            throw new InvalidDataException("Storage contract must be explicitly accepted as sqlite-local-v1.");
        try
        {
            string data = ExistingDirectory(dataDirectory);
            string anchor = ExistingDirectory(anchorDirectory);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (Within(data, anchor, comparison) || Within(anchor, data, comparison))
                throw new InvalidDataException("Storage directories overlap.");
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                throw new InvalidDataException("Storage contract is unsupported on this operating system.");
            return new(data, anchor, ObserveFileSystem(data, qualifiedLinuxFileSystem), ObserveFileSystem(anchor, qualifiedLinuxFileSystem));
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new InvalidDataException("Storage profile could not be established from actual filesystem metadata.", error);
        }
    }

    private static string ExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Storage path must be an explicit local absolute non-UNC directory.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new InvalidDataException("Storage directory is not provisioned.");

        // Walk the original spelling before collapsing '..': otherwise a traversed link could disappear.
        string root = Path.GetPathRoot(path)!;
        string cursor = root;
        RejectLink(cursor);
        foreach (string component in path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            if (component == "..")
            {
                cursor = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(cursor)) ?? root;
                continue;
            }
            if (OperatingSystem.IsWindows())
            {
                // Enumerating an exact existing name returns its long spelling, closing 8.3 alias overlaps.
                // Ambiguous DOS matching is refused rather than selecting an arbitrary directory.
                string[] matches = Directory.GetDirectories(cursor, component, SearchOption.TopDirectoryOnly);
                if (matches.Length != 1) throw new InvalidDataException("Storage directory identity is ambiguous or not provisioned.");
                cursor = matches[0];
            }
            else cursor = Path.Combine(cursor, component);
            RejectLink(cursor);
        }
        if (!Directory.Exists(cursor)) throw new InvalidDataException("Storage directory is not provisioned.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(cursor));
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Storage paths and ancestors must not contain links or reparse points.");
    }

    private static bool Within(string candidate, string parent, StringComparison comparison) =>
        candidate.Equals(parent, comparison) || candidate.StartsWith(
            Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);

    private static OutpostInitializeFileSystemFacts ObserveFileSystem(string path, string qualifiedLinuxFileSystem)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Select the longest actual mount before querying its format. Never fall back to a parent's filesystem
        // when a more specific mount is unsupported, unready, or unreadable.
        var mounts = DriveInfo.GetDrives().Select(d => new {
            Drive = d, Mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(d.Name))
        }).Where(d => Within(path, d.Mount, comparison)).OrderByDescending(d => d.Mount.Length).ToArray();
        if (mounts.Length == 0 || (mounts.Length > 1 && mounts[0].Mount.Length == mounts[1].Mount.Length))
            throw new InvalidDataException("Storage filesystem mount identity is missing or ambiguous.");
        DriveInfo drive = mounts[0].Drive;
        string actual = drive.DriveFormat;
        if (OperatingSystem.IsWindows())
        {
            if (drive.DriveType != DriveType.Fixed || !string.Equals(actual, "NTFS", StringComparison.Ordinal))
                throw new InvalidDataException("Storage filesystem must be actual fixed local NTFS on Windows.");
        }
        else if (actual is not ("ext4" or "xfs" or "btrfs") ||
            !string.Equals(actual, qualifiedLinuxFileSystem, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Storage filesystem must be explicitly qualified as its actual local ext4, xfs, or btrfs mount.");
        }
        return new(mounts[0].Mount, actual);
    }
}
