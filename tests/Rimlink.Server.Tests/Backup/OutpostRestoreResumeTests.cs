using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreResumeTests
{
    [Theory]
    [InlineData("AfterJournalCreated")]
    [InlineData("AfterExternalFence")]
    [InlineData("AfterStagedVerified")]
    [InlineData("AfterStageFile:")]
    [InlineData("AfterRetainFile:")]
    [InlineData("AfterOldSetRetained")]
    [InlineData("AfterSwitchPlanned:rimlink.db")]
    [InlineData("AfterSwitchEffect:rimlink.db")]
    [InlineData("AfterSwitchCompleted:rimlink.db")]
    public async Task Resume_continues_exact_journal_without_new_fence_or_asset_replay(string stop)
    {
        var (f, request, anchor) = await Interrupt(stop);
        using (f)
        {
            string root = Path.Combine(anchor, "restores", request.RestoreId);
            byte[] intent = File.ReadAllBytes(Path.Combine(root, "intent.json"));
            var procedure = new OutpostRestoreProcedure
            { Checkpoint = point => { if (point == "AfterInstalledVerified") throw new StopRestore(); } };
            await Assert.ThrowsAsync<StopRestore>(() => procedure.ResumeAsync(request.Inspection.DataDirectory, anchor, request.RestoreId, default));
            Assert.Equal(intent, File.ReadAllBytes(Path.Combine(root, "intent.json")));
            var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(Path.Combine(root, "journal.json")), OutpostBackupManifest.Json)!;
            Assert.Equal(RestorePhase.InstalledVerified, journal.Phase);
            Assert.Equal(SHA256.HashData(intent), journal.IntentDigest);
            foreach (var expected in request.Inspection.Manifest.Files)
                Assert.Equal(expected.Sha256, SHA256.HashData(File.ReadAllBytes(Path.Combine(request.Inspection.DataDirectory, expected.Path.Replace('/', Path.DirectorySeparatorChar)))));
            f.H.Db.ChangeTracker.Clear();
            Assert.Equal(0, (await f.H.Row()).Stock);
            Assert.Equal(request.Inspection.Manifest.Authority.HeadId, (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
            var observed = await procedure.InspectAsync(request.Inspection.ArchivePath, request.Inspection.DataDirectory, anchor, default);
            Assert.Equal(request.RestoreId, observed.Destination.External!.Restore!.Id);
            Assert.Equal(request.TargetBoundary, observed.Destination.External.Restore.NewBoundary);
            Assert.Equal(request.Inspection.Destination.External!.Committed, observed.Destination.External.Committed);
        }
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("changed-intent")]
    [InlineData("changed-archive")]
    [InlineData("changed-staged")]
    public async Task Resume_never_replaces_live_bytes_when_selected_evidence_changed(string damage)
    {
        var (f, request, anchor) = await Interrupt("AfterOldSetRetained");
        using (f)
        {
            string root = Path.Combine(anchor, "restores", request.RestoreId);
            if (damage == "changed-intent")
            {
                string path = Path.Combine(root, "intent.json");
                File.WriteAllText(path, File.ReadAllText(path).Replace(request.TargetBoundary, Guid.NewGuid().ToString("D")));
            }
            if (damage == "changed-archive") File.AppendAllText(request.Inspection.ArchivePath, "changed");
            if (damage == "changed-staged") File.WriteAllBytes(Path.Combine(request.Inspection.DataDirectory, "restore-work", request.RestoreId, "staged", "rimlink.db"), [1, 2, 3]);
            string before = await f.Cut();
            byte[] external = File.ReadAllBytes(Path.Combine(anchor, "authority.db"));
            var result = await new OutpostRestoreProcedure().ResumeAsync(request.Inspection.DataDirectory, anchor,
                damage == "wrong-id" ? Guid.NewGuid().ToString("D") : request.RestoreId, default);
            Assert.True(result.Outcome is RestoreOutcome.Refused or RestoreOutcome.Maintenance, result.ToString());
            Assert.NotEmpty(result.Reason);
            Assert.Equal(before, await f.Cut());
            Assert.Equal(external, File.ReadAllBytes(Path.Combine(anchor, "authority.db")));
        }
    }

    internal static async Task<(LifecycleFixture Fixture, RestoreRequest Request, string Anchor)> Interrupt(string stop)
    {
        var f = await LifecycleFixture.Create();
        try
        {
            string archive = await OutpostBackupTests.Create(f);
            f.H.F.Time.Advance(TimeSpan.FromHours(1));
            await f.H.Service.StateAsync(1, new OutpostStateQuery { Scope = f.H.Scope.Clone() }, default);
            var shared = f.H.F.H.H;
            string anchor = Path.GetDirectoryName(shared.Anchor.DatabasePath)!;
            var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == stop || (stop.EndsWith(':') && point.StartsWith(stop, StringComparison.Ordinal))) throw new StopRestore(); } };
            var inspected = await procedure.InspectAsync(archive, shared.Data.Root, anchor, default);
            var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
                inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(shared.Data.Root));
            request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
            f.Host.Dispose();
            await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
            return (f, request, anchor);
        }
        catch { f.Dispose(); throw; }
    }

    private sealed class StopRestore : Exception;
}
