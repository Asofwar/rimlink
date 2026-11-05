using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreFenceTests
{
    [Theory]
    [InlineData("AfterJournalCreated", false)]
    [InlineData("AfterExternalFence", true)]
    public async Task Restore_journals_exact_cut_and_actual_external_before_any_replacement(string stop, bool fenced)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        await f.H.Service.StateAsync(1, new OutpostStateQuery { Scope = f.H.Scope.Clone() }, default);
        var shared = f.H.F.H.H;
        string anchor = Path.GetDirectoryName(shared.Anchor.DatabasePath)!;
        var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == stop) throw new StopRestore(); } };
        var inspected = await procedure.InspectAsync(archive, shared.Data.Root, anchor, default);
        var external = Assert.IsType<AnchorRecord>(inspected.Destination.External);
        Assert.NotEqual(inspected.Manifest.Authority.HeadId, external.Committed.Id);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(shared.Data.Root));
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        f.Host.Dispose();
        string before = await f.Cut();
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        Assert.Equal(before, await f.Cut()); // Neither DB, saves, stock nor original proof rows have changed.
        string journalRoot = Path.Combine(anchor, "restores", request.RestoreId);
        byte[] intent = File.ReadAllBytes(Path.Combine(journalRoot, "intent.json"));
        using var plan = JsonDocument.Parse(intent);
        Assert.Equal(1, plan.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(request.RestoreId, plan.RootElement.GetProperty("selection").GetProperty("restoreId").GetString());
        Assert.Equal(inspected.Manifest.Authority.HeadId, plan.RootElement.GetProperty("source").GetProperty("headId").GetString());
        var durableSelection = plan.RootElement.GetProperty("selection");
        Assert.Equal(AnchorRecordCodec.Encode(external), durableSelection.GetProperty("externalRecord").GetBytesFromBase64());
        Assert.Equal(request.ConfirmationDigest, plan.RootElement.GetProperty("selectionDigest").GetBytesFromBase64());
        Assert.True(File.Exists(Path.Combine(journalRoot, "journal.json")));
        var observed = await procedure.InspectAsync(archive, shared.Data.Root, anchor, default);
        var actual = Assert.IsType<AnchorRecord>(observed.Destination.External);
        Assert.Equal(external.Committed, actual.Committed); // E stays E; C must NOT be substituted for it.
        Assert.Null(actual.Pending);
        if (fenced)
        {
            Assert.Equal(RestoreAnchorState.RestoreInProgress, observed.Destination.AnchorState);
            Assert.Equal(request.RestoreId, actual.Restore?.Id);
            Assert.Equal(request.TargetBoundary, actual.Restore?.NewBoundary);
            Assert.Equal(SHA256.HashData(intent), actual.Restore?.BackupDigest.ToByteArray());
            var opened = await shared.Authority.OpenAsync(default);
            Assert.Equal(OutpostStartupState.Maintenance, opened.State);
            Assert.Equal("anchor-reconciliation-required", opened.Reason);
        }
        else
        {
            Assert.Equal(RestoreAnchorState.Committed, observed.Destination.AnchorState);
            Assert.Null(actual.Restore);
        }
    }

    [Fact]
    public async Task Io_after_durable_fence_is_maintenance_not_business_refusal()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var shared = f.H.F.H.H;
        string anchor = Path.GetDirectoryName(shared.Anchor.DatabasePath)!;
        var procedure = new OutpostRestoreProcedure
        { Checkpoint = point => { if (point == "AfterExternalFence") throw new IOException("owned-fixture-interrupted-after-fence"); } };
        var inspected = await procedure.InspectAsync(archive, shared.Data.Root, anchor, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(shared.Data.Root));
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        f.Host.Dispose();
        string before = await f.Cut();
        var result = await procedure.ExecuteAsync(request, default);
        Assert.Equal(RestoreOutcome.Maintenance, result.Outcome);
        Assert.Equal(request.RestoreId, result.RestoreId);
        Assert.Contains("restore-interrupted", result.Reason);
        Assert.Equal(before, await f.Cut());
        Assert.Equal(RestoreAnchorState.RestoreInProgress, (await procedure.InspectAsync(archive, shared.Data.Root, anchor, default)).Destination.AnchorState);
    }

    private sealed class StopRestore : Exception;
}
