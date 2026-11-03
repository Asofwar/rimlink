using Rimlink.Client.Mods.Delivery;
using Rimlink.Protocol;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Mods;

public sealed class ModpackInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rl-install-" + Guid.NewGuid().ToString("N"));
    public ModpackInstallerTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private InstalledDeliveryMod Core => new("ludeon.rimworld", "ludeon.rimworld", Path.Combine(root, "game", "Data", "Core"), "", new("", false, false, false));

    [Fact]
    public void RecoveryWithNoPendingStateDoesNotNeedAWritableGameDirectory()
    {
        var (installer, _, _) = Prepare(); string workspace = Path.Combine(root, "game", ".rimlink-modpack");
        if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        File.WriteAllText(workspace, "cannot create an install directory here");
        Assert.False(installer.Recover([]));
        Assert.Equal("cannot create an install directory here", File.ReadAllText(workspace));
    }

    [Fact]
    public void AGlobalPendingMarkerStillBlocksAnotherProfileWithoutItsOwnJournal()
    {
        var (installer, _, _) = Prepare(); string workspace = Path.Combine(root, "game", ".rimlink-modpack"); Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "pending.json"), "another profile owns an unfinished operation");
        Assert.Equal("RecoveryRequired", Assert.Throws<DeliveryException>(() => installer.Recover([])).Code);
    }

    [Fact]
    public void InstallsTopLevelFoldersAndWritesConfigLastWithoutTouchingPersonalFiles()
    {
        var (installer, package, source) = Prepare();
        var plan = installer.Plan(package, [Core], []);
        var events = new List<string>(); installer.AfterStep = events.Add;
        installer.Commit(plan, package, "test", "Test", null, [], CancellationToken.None);
        Assert.Equal("AwaitingRestart", installer.State);
        string installed = Assert.Single(Directory.GetDirectories(Path.Combine(root, "game", "Mods")));
        Assert.StartsWith("rimlink-managed-", Path.GetFileName(installed));
        Assert.Equal(ModContent.Scan(source), ModContent.Scan(installed));
        Assert.Equal("File:ModsConfig", events.Last());
        Assert.Contains("test.demo", File.ReadAllText(Path.Combine(root, "profile", "Config", "ModsConfig.xml")));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(root, "profile", "Config", "Prefs.xml")));
    }

    [Theory]
    [InlineData("Move:Install")]
    [InlineData("File:ActiveSettings")]
    [InlineData("File:Ownership")]
    [InlineData("File:ModsConfig")]
    public void InterruptedCommitRestoresOriginalConfigAndRemovesOnlyItsOwnResults(string failAt)
    {
        var (installer, package, _) = Prepare();
        string config = Path.Combine(root, "profile", "Config", "ModsConfig.xml"); var original = File.ReadAllBytes(config);
        var plan = installer.Plan(package, [Core], []);
        installer.AfterStep = step => { if (step == failAt) throw new IOException("test interruption"); };
        Assert.Throws<IOException>(() => installer.Commit(plan, package, "test", "Test", null, [], CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "game", "Mods")));
    }

    [Fact]
    public void ForeignModConflictNeverOverwritesTheExistingCopy()
    {
        var (installer, package, _) = Prepare();
        string foreign = Path.Combine(root, "game", "Mods", "foreign"); Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "mine.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(foreign, "About"));
        File.WriteAllText(Path.Combine(foreign, "About", "About.xml"), "<ModMetaData><packageId>test.demo</packageId><name>Demo</name><modVersion>0</modVersion><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
        var conflict = new InstalledDeliveryMod("test.demo", "test.demo", foreign, "0", new ModContentInfo(new string('b', 64), true, false, false));
        Assert.Equal("Conflict", Assert.Throws<DeliveryException>(() => installer.Plan(package, [Core, conflict], [])).Code);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(foreign, "mine.txt")));
    }

    [Fact]
    public void AnExternalConfigEditStopsRecoveryAndBothVersionsRemainAvailable()
    {
        var (installer, package, _) = Prepare(); var plan = installer.Plan(package, [Core], []);
        string config = Path.Combine(root, "profile", "Config", "ModsConfig.xml"); string original = File.ReadAllText(config);
        installer.AfterStep = step => { if (step == "File:Ownership") File.WriteAllText(config, "external edit"); };
        Assert.Equal("ExternalChange", Assert.Throws<DeliveryException>(() => installer.Commit(plan, package, "test", "Test", null, [], CancellationToken.None)).Code);
        Assert.Equal("external edit", File.ReadAllText(config)); Assert.Equal("RecoveryRequired", installer.State);
        Assert.Contains(Directory.GetFiles(Path.Combine(root, "profile", "Rimlink", "installs"), "ModsConfig.bak", SearchOption.AllDirectories), path => File.ReadAllText(path) == original);
    }

    [Fact]
    public void AForeignCopyWithDifferentTexturesIsNotAnExactReusableCopy()
    {
        var (installer, package, source) = Prepare(); string foreign = Path.Combine(root, "game", "Mods", "foreign"); CopyTree(source, foreign);
        Directory.CreateDirectory(Path.Combine(foreign, "Textures")); File.WriteAllText(Path.Combine(foreign, "Textures", "changed.txt"), "keep foreign cosmetic content");
        Assert.Equal(ModContent.Scan(source).FilesHash, ModContent.Scan(foreign).FilesHash);
        var copy = new InstalledDeliveryMod("test.demo", "test.demo", foreign, "1", ModContent.Scan(foreign));
        Assert.Equal("Conflict", Assert.Throws<DeliveryException>(() => installer.Plan(package, [Core, copy], [])).Code);
        Assert.Equal("keep foreign cosmetic content", File.ReadAllText(Path.Combine(foreign, "Textures", "changed.txt")));
    }

    [Fact]
    public void CancellationAfterTheLastWriteStillRollsBackAndMissingConfigStaysMissing()
    {
        var (installer, package, _) = Prepare(); string config = Path.Combine(root, "profile", "Config", "ModsConfig.xml"); File.Delete(config);
        var plan = installer.Plan(package, [Core], []); using var cancel = new CancellationTokenSource();
        installer.AfterStep = step => { if (step == "File:ModsConfig") cancel.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => installer.Commit(plan, package, "test", "Test", null, [], cancel.Token));
        Assert.False(File.Exists(config)); Assert.Null(installer.State);
    }

    [Fact]
    public void TheInstallLockIsSharedBetweenDifferentProfilesOfTheSameGame()
    {
        var (installer, _, _) = Prepare();
        using var other = new ModpackInstaller(Path.Combine(root, "game", "Mods"), Path.Combine(root, "other", "Config", "ModsConfig.xml"), Path.Combine(root, "other", "Rimlink"));
        using (installer.Acquire()) Assert.Equal("Busy", Assert.Throws<DeliveryException>(() => other.Acquire()).Code);
        using var free = other.Acquire();
    }

    [Fact]
    public void GameAndConfigOnDifferentDrivesRollBackTogether()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists("D:\\Build")) return;
        var (_, package, _) = Prepare();
        string testRoot = Path.Combine("D:\\Build", "rimlink-install-tests", Guid.NewGuid().ToString("N"));
        string mods = Path.Combine(testRoot, "game", "Mods"); Directory.CreateDirectory(mods);
        try
        {
            string config = Path.Combine(root, "profile", "Config", "ModsConfig.xml"); var original = File.ReadAllBytes(config);
            using var installer = new ModpackInstaller(mods, config, Path.Combine(root, "profile", "Rimlink"));
            var plan = installer.Plan(package, [Core], []);
            installer.AfterStep = step => { if (step == "File:ActiveSettings") throw new IOException("test cross-drive interruption"); };
            Assert.Throws<IOException>(() => installer.Commit(plan, package, "test", "Test", null, [], CancellationToken.None));
            Assert.Equal(original, File.ReadAllBytes(config)); Assert.Empty(Directory.GetDirectories(mods)); Assert.Null(installer.State);
            Assert.NotEqual(Path.GetPathRoot(mods), Path.GetPathRoot(config));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath("D:\\Build\\rimlink-install-tests\\"), Path.GetFullPath(testRoot), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public void ALoadedManagedRootCannotBeRetiredEvenIfTheOsWouldRenameIt()
    {
        var (installer, package, _) = Prepare(); var initial = installer.Plan(package, [Core], []);
        installer.Commit(initial, package, "test", "Test", null, [], CancellationToken.None);
        var loaded = initial.Future.Select((m, i) => m.Reference(i)).ToList(); installer.LaunchId = Guid.NewGuid().ToString("N");
        Assert.True(installer.VerifyAfterRestart(loaded, [], initial.Future.ToDictionary(m => m.PackageId, m => m.Root), true));
        var previous = Assert.Single(initial.Mods); string oldRoot = initial.Future.Single(m => m.PackageId == "test.demo").Root;
        // Force a legitimate update by using another prepared version, without changing the owned old root.
        string source = Path.Combine(root, "prepared", "demo"); File.WriteAllText(Path.Combine(source, "Defs", "a.xml"), "<Defs><!--new--></Defs>");
        var descriptor = DeliveryValidation.Describe("1.7", [new(0, "ludeon.rimworld", "", "", EntryKind.Required), new(1, "test.demo", "1", ModContent.Scan(source).FilesHash, EntryKind.Required)], []);
        var packed = SafeModArchive.PackPreparedCollection(Path.GetDirectoryName(source)!, descriptor, Path.Combine(root, "update")); var manifest = packed.Manifest with { PackageId = Guid.NewGuid().ToString("N") };
        var update = new DownloadedModpack(manifest, new(manifest.PackageId, manifest.CatalogHash, []), packed.ArchivePath);
        var installed = new InstalledDeliveryMod("test.demo", "test.demo", oldRoot, "1", ModContent.Scan(oldRoot));
        var plan = installer.Plan(update, [Core, installed], ["ludeon.rimworld", "test.demo"]);
        Assert.Equal("LoadedMod", Assert.Throws<DeliveryException>(() => installer.Commit(plan, update, "test", "Test", null, [oldRoot], CancellationToken.None)).Code);
        Assert.True(Directory.Exists(oldRoot)); Assert.Null(installer.State);
    }

    [Theory]
    [InlineData("Move:Install")]
    [InlineData("File:ActiveSettings")]
    [InlineData("File:Ownership")]
    [InlineData("File:ModsConfig")]
    public void ANewInstallerRecoversTheExactFilesystemSnapshotOfAnInterruptedProcess(string point)
    {
        var (installer, package, _) = Prepare(); var plan = installer.Plan(package, [Core], []);
        string config = Path.Combine(root, "profile", "Config", "ModsConfig.xml"); byte[] original = File.ReadAllBytes(config);
        string snapshot = Path.Combine(Path.GetTempPath(), "rl-checkpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            installer.AfterStep = step => { if (step == point) { CopyTree(root, snapshot); throw new IOException("test checkpoint"); } };
            Assert.Throws<IOException>(() => installer.Commit(plan, package, "test", "Test", null, [], CancellationToken.None));
            installer.Dispose(); Directory.Delete(root, true); CopyTree(snapshot, root);
            using var restarted = new ModpackInstaller(Path.Combine(root, "game", "Mods"), config, Path.Combine(root, "profile", "Rimlink"));
            Assert.False(restarted.Recover([])); Assert.Null(restarted.State);
            Assert.Equal(original, File.ReadAllBytes(config)); Assert.Empty(Directory.GetDirectories(Path.Combine(root, "game", "Mods")));
        }
        finally { if (Directory.Exists(snapshot)) Directory.Delete(snapshot, true); }
    }
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "install.lock") continue;
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    private (ModpackInstaller Installer, DownloadedModpack Package, string Source) Prepare()
    {
        string mods = Path.Combine(root, "game", "Mods"), profile = Path.Combine(root, "profile"), source = Path.Combine(root, "prepared", "demo");
        Directory.CreateDirectory(mods); Directory.CreateDirectory(Path.Combine(profile, "Config")); Directory.CreateDirectory(Path.Combine(source, "About"));
        Directory.CreateDirectory(Core.Root);
        File.WriteAllText(Path.Combine(profile, "Config", "ModsConfig.xml"), "<ModsConfigData><version>1.7</version><activeMods /></ModsConfigData>");
        File.WriteAllText(Path.Combine(profile, "Config", "Prefs.xml"), "personal");
        File.WriteAllText(Path.Combine(source, "About", "About.xml"), "<ModMetaData><packageId>test.demo</packageId><name>Demo</name><modVersion>1</modVersion><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
        Directory.CreateDirectory(Path.Combine(source, "Defs")); File.WriteAllText(Path.Combine(source, "Defs", "a.xml"), "<Defs/>");
        var descriptor = DeliveryValidation.Describe("1.7", [new(0, "ludeon.rimworld", "", "", EntryKind.Required), new(1, "test.demo", "1", ModContent.Scan(source).FilesHash, EntryKind.Required)], []);
        var packed = SafeModArchive.PackPreparedCollection(Path.GetDirectoryName(source)!, descriptor, Path.Combine(root, "packed"));
        var manifest = packed.Manifest with { PackageId = Guid.NewGuid().ToString("N") };
        var package = new DownloadedModpack(manifest, new SettingsSnapshot(manifest.PackageId, manifest.CatalogHash, []), packed.ArchivePath);
        return (new ModpackInstaller(mods, Path.Combine(profile, "Config", "ModsConfig.xml"), Path.Combine(profile, "Rimlink")), package, source);
    }
}
