using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Protocol.V1;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreStagingTests
{
    [Theory]
    [InlineData("AfterStagedVerified", false)]
    [InlineData("AfterOldSetRetained", true)]
    public async Task Restore_verifies_staged_cut_and_retains_actual_old_set_before_switch(string stop, bool retained)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        await f.H.Service.StateAsync(1, new OutpostStateQuery { Scope = f.H.Scope.Clone() }, default);
        var shared = f.H.F.H.H;
        string anchor = Path.GetDirectoryName(shared.Anchor.DatabasePath)!;
        var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == stop) throw new StopRestore(); } };
        var inspected = await procedure.InspectAsync(archive, shared.Data.Root, anchor, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(shared.Data.Root));
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        f.Host.Dispose();
        string before = await f.Cut();
        var oldFiles = new[] { shared.Data.DatabasePath }.Concat(new[] { shared.Data.SavesRoot, shared.Data.Archive }
            .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))).ToDictionary(
                path => Path.GetRelativePath(shared.Data.Root, path), path => SHA256.HashData(File.ReadAllBytes(path)));
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        Assert.Equal(before, await f.Cut());
        string work = Path.Combine(shared.Data.Root, "restore-work", request.RestoreId);
        foreach (var file in inspected.Manifest.Files)
        {
            string staged = Path.Combine(work, "staged", file.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(file.Length, new FileInfo(staged).Length);
            Assert.Equal(file.Sha256, SHA256.HashData(File.ReadAllBytes(staged)));
        }
        string journalRoot = Path.Combine(anchor, "restores", request.RestoreId);
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(Path.Combine(journalRoot, "journal.json")), OutpostBackupManifest.Json)!;
        Assert.Equal(retained ? RestorePhase.OldSetRetained : RestorePhase.StagedVerified, journal.Phase);
        if (retained)
        {
            foreach (var (path, hash) in oldFiles) Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Path.Combine(work, "retained", path))));
            using var stored = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(journalRoot, "journal.json")));
            Assert.True(stored.RootElement.GetProperty("oldSetUsable").GetBoolean());
        }
        Assert.Equal(RestoreAnchorState.RestoreInProgress, (await procedure.InspectAsync(archive, shared.Data.Root, anchor, default)).Destination.AnchorState);
    }

    [Theory]
    [InlineData("missing-required-save")]
    [InlineData("corrupt-main")]
    public async Task Retaining_bytes_does_not_mark_a_broken_old_set_usable(string damage)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var shared = f.H.F.H.H;
        string anchor = Path.GetDirectoryName(shared.Anchor.DatabasePath)!;
        var procedure = new OutpostRestoreProcedure
        { Checkpoint = point => { if (point == "AfterOldSetRetained") throw new StopRestore(); } };
        var inspected = await procedure.InspectAsync(archive, shared.Data.Root, anchor, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(shared.Data.Root));
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        f.Host.Dispose();
        if (damage == "corrupt-main") File.WriteAllBytes(shared.Data.DatabasePath, [1, 2, 3]);
        else File.Delete(shared.Files.FullPath(1, f.H.Post.Version.Number));
        byte[] before = SHA256.HashData(File.ReadAllBytes(shared.Data.DatabasePath));
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(shared.Data.DatabasePath)));
        string path = Path.Combine(anchor, "restores", request.RestoreId, "journal.json");
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(path), OutpostBackupManifest.Json)!;
        Assert.False(journal.OldSetUsable);
        Assert.NotEmpty(journal.OldSetReason);
        Assert.Equal(RestorePhase.OldSetRetained, journal.Phase);
    }

    private sealed class StopRestore : Exception;
}
