using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreSwitchTests
{
    [Fact]
    public async Task Installed_cut_matches_backup_not_newer_external_and_keeps_verified_old_set()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        await f.H.Service.StateAsync(1, new OutpostStateQuery { Scope = f.H.Scope.Clone() }, default);
        var shared = f.H.F.H.H;
        string anchor = Path.GetDirectoryName(shared.Anchor.DatabasePath)!;
        var procedure = new OutpostRestoreProcedure
        { Checkpoint = point => { if (point == "AfterInstalledVerified") throw new StopRestore(); } };
        var inspected = await procedure.InspectAsync(archive, shared.Data.Root, anchor, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(shared.Data.Root));
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        f.Host.Dispose();
        string staleOnly = Path.Combine(shared.Data.SavesRoot, "1", "unreferenced-after-cut.bin");
        File.WriteAllBytes(staleOnly, [4, 5, 6]);
        byte[] previousDb = SHA256.HashData(File.ReadAllBytes(shared.Data.DatabasePath));
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        foreach (var expected in inspected.Manifest.Files)
            Assert.Equal(expected.Sha256, SHA256.HashData(File.ReadAllBytes(Path.Combine(shared.Data.Root, expected.Path.Replace('/', Path.DirectorySeparatorChar)))));
        Assert.False(File.Exists(staleOnly));
        string work = Path.Combine(shared.Data.Root, "restore-work", request.RestoreId);
        Assert.Equal(previousDb, SHA256.HashData(File.ReadAllBytes(Path.Combine(work, "retained", "rimlink.db"))));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(work, "retained", "saves", "1", "unreferenced-after-cut.bin")));
        f.H.Db.ChangeTracker.Clear();
        Assert.Equal(inspected.Manifest.Authority.HeadId, (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Equal(0, (await f.H.Row()).Stock);
        var actual = await procedure.InspectAsync(archive, shared.Data.Root, anchor, default);
        Assert.Equal(RestoreAnchorState.RestoreInProgress, actual.Destination.AnchorState);
        Assert.Equal(inspected.Destination.External!.Committed, actual.Destination.External!.Committed);
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(Path.Combine(anchor, "restores", request.RestoreId, "journal.json")), OutpostBackupManifest.Json)!;
        Assert.Equal(RestorePhase.InstalledVerified, journal.Phase);
    }

    private sealed class StopRestore : Exception;
}
