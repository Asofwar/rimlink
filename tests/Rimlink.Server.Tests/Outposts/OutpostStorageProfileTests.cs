using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

// Real local filesystem observations, not evidence of power-loss durability or another OS's runtime stack.
public sealed class OutpostStorageProfileTests
{
    [Fact]
    public void Genuine_current_filesystem_is_accepted_only_when_it_matches_the_supported_profile()
    {
        using var f = new Fixture();
        DriveInfo actual = CoveringDrive(f.Data);
        string format = actual.DriveFormat;
        if (OperatingSystem.IsWindows() && actual.DriveType == DriveType.Fixed && format == "NTFS")
        {
            OutpostStorageProfile.Validate(f.Data, f.Anchor, "sqlite-local-v1", "");
            Assert.Empty(Directory.GetFileSystemEntries(f.Data));
            Assert.Empty(Directory.GetFileSystemEntries(f.Anchor));
        }
        else if (OperatingSystem.IsLinux() && format is "ext4" or "xfs" or "btrfs")
        {
            OutpostStorageProfile.Validate(f.Data, f.Anchor, "sqlite-local-v1", format);
            Assert.Empty(Directory.GetFileSystemEntries(f.Data));
            Assert.Empty(Directory.GetFileSystemEntries(f.Anchor));
        }
        else
        {
            // Overlay/FUSE/NFS/tmpfs and unsupported OS/filesystems must not become accepted by naming them.
            Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(f.Data, f.Anchor, "sqlite-local-v1", format));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("sqlite-local-v2")]
    [InlineData("SQLITE-LOCAL-V1")]
    [InlineData(" sqlite-local-v1")]
    public void Explicit_exact_operator_contract_is_required(string contract)
    {
        using var f = new Fixture();
        var error = Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(f.Data, f.Anchor, contract, CurrentQualification(f.Data)));
        Assert.Contains("contract", error.Message);
    }

    [Theory]
    [InlineData(0)] // equal
    [InlineData(1)] // anchor below data
    [InlineData(2)] // data below anchor
    [InlineData(3)] // normalization does not conceal equality
    public void Equal_nested_and_normalized_overlaps_are_refused(int kind)
    {
        using var f = new Fixture();
        string child = Path.Combine(f.Data, "child");
        Directory.CreateDirectory(child);
        (string data, string anchor) = kind switch {
            0 => (f.Data, f.Data), 1 => (f.Data, child), 2 => (child, f.Data),
            _ => (f.Data, Path.Combine(f.Data, "child", ".."))
        };
        var error = Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(data, anchor, "sqlite-local-v1", CurrentQualification(data)));
        Assert.Contains("overlap", error.Message);
    }

    [Fact]
    public void Sibling_names_with_same_prefix_are_not_nested()
    {
        using var f = new Fixture();
        string sibling = f.Data + "-anchor";
        Directory.CreateDirectory(sibling);
        DriveInfo actual = CoveringDrive(f.Data);
        bool supported = OperatingSystem.IsWindows() ? actual.DriveType == DriveType.Fixed && actual.DriveFormat == "NTFS"
            : OperatingSystem.IsLinux() && actual.DriveFormat is "ext4" or "xfs" or "btrfs";
        if (supported) OutpostStorageProfile.Validate(f.Data, sibling, "sqlite-local-v1", CurrentQualification(f.Data));
        else Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(f.Data, sibling, "sqlite-local-v1", CurrentQualification(f.Data)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_directories_are_refused_without_creation(bool missingData)
    {
        using var f = new Fixture();
        string missing = Path.Combine(f.Root, "missing");
        var error = Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(
            missingData ? missing : f.Data, missingData ? f.Anchor : missing, "sqlite-local-v1", CurrentQualification(f.Data)));
        Assert.Contains("provisioned", error.Message);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData("relative-data")]
    [InlineData("")]
    [InlineData("\\\\server\\share\\data")]
    [InlineData("//server/share/data")]
    public void Relative_empty_or_UNC_paths_are_refused(string path)
    {
        using var f = new Fixture();
        var dataError = Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(path, f.Anchor, "sqlite-local-v1", ""));
        var anchorError = Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(f.Data, path, "sqlite-local-v1", ""));
        Assert.Contains("absolute", dataError.Message);
        Assert.Contains("absolute", anchorError.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Symlink_directory_or_ancestor_is_refused(bool linkIsData)
    {
        using var f = new Fixture();
        string target = Path.Combine(f.Root, "target");
        string child = Path.Combine(target, "child");
        string link = Path.Combine(f.Root, "link");
        Directory.CreateDirectory(child);
        // Intentionally exercise real link metadata, not a fake path classifier; missing link privileges fail visibly.
        Directory.CreateSymbolicLink(link, target);
        try
        {
            string throughAncestor = Path.Combine(link, "child");
            foreach (string linkedPath in new[] { link, throughAncestor })
            {
                var error = Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(
                    linkIsData ? linkedPath : f.Data, linkIsData ? f.Anchor : linkedPath,
                    "sqlite-local-v1", CurrentQualification(f.Data)));
                Assert.Contains("link", error.Message);
            }
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void Linux_requires_exact_explicit_actual_filesystem_qualification()
    {
        if (!OperatingSystem.IsLinux()) return; // No Linux runtime claim when this suite runs on Windows.
        using var f = new Fixture();
        string actual = CoveringDrive(f.Data).DriveFormat;
        foreach (string qualification in new[] { "", "NTFS", actual.ToUpperInvariant(), actual + " " })
            Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(f.Data, f.Anchor, "sqlite-local-v1", qualification));
        if (actual is not ("ext4" or "xfs" or "btrfs"))
            Assert.Throws<InvalidDataException>(() => OutpostStorageProfile.Validate(f.Data, f.Anchor, "sqlite-local-v1", actual));
    }

    private static string CurrentQualification(string path) => Support.LocalTestStorage.LinuxQualification(path);

    private static DriveInfo CoveringDrive(string path)
    {
        if (OperatingSystem.IsWindows()) return new DriveInfo(Path.GetPathRoot(path)!);
        string full = Path.GetFullPath(path);
        return DriveInfo.GetDrives().Where(d => {
            string mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(d.Name));
            return full == mount || full.StartsWith(Path.EndsInDirectorySeparator(mount) ? mount : mount + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }).OrderByDescending(d => d.Name.Length).First();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rimlink-storage-profile-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "data");
        public string Anchor => Path.Combine(Root, "anchor");
        public Fixture() { Directory.CreateDirectory(Data); Directory.CreateDirectory(Anchor); }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
