using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

/// <summary>Owned process termination, not a claim about power-loss durability.</summary>
public sealed class OutpostRestoreAdditiveCrashTests
{
    [Theory]
    [InlineData("AfterMainPlanWritten")]
    [InlineData("AfterMainPlanBound")]
    [InlineData("BeforeSchemaMigration")]
    [InlineData("AfterSchemaMigration")]
    [InlineData("AfterMainDatabaseCommit")]
    [InlineData("AfterCompletionCommitted")]
    public async Task Killed_additive_restore_resumes_exact_bound_plan_and_imports_C_once(string checkpoint)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await OutpostRestoreSchemaMigrationTests.PreAdminCut(f);
        byte[] archiveDigest = SHA256.HashData(File.ReadAllBytes(archive));
        byte[] prefix = OutpostRestoreEvidence.Encode(await h.Db.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync());
        var request = await OutpostRestoreTests.Request(f, new OutpostRestoreProcedure(), archive);
        string input = Path.Combine(h.Data.Backups, "additive-crash-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new WorkerRequest(archive, h.Data.Root,
            request.Inspection.AnchorDirectory, request.RestoreId, request.TargetInstallationId, request.TargetBoundary,
            request.ConfirmationDigest, request.QualifiedLinuxFileSystem), OutpostBackupManifest.Json));
        f.Host.Dispose(); await h.MainObservation.DrainAsync(default); await h.Db.Database.CloseConnectionAsync();
        var termination = await OutpostFaults.KillAtAsync(h.Data.Root, checkpoint,
            workerTest: "Rimlink.Server.Tests.Backup.OutpostRestoreCrashTests.Owned_restore_crash_worker",
            workerEnvironment: new Dictionary<string, string> { ["RIMLINK_OUTPOST_RESTORE_FAULT_REQUEST"] = input });
        Assert.True(termination.WorkerExitedAfterRoot, termination.ToString()); Assert.True(termination.WorkerExitedAfterStreams, termination.ToString());
        string path = Path.Combine(request.Inspection.AnchorDirectory, "restores", request.RestoreId, "main.json");
        byte[] frozen = File.ReadAllBytes(path); var recorded = OutpostRestoreEvidence.DecodePlan(frozen);
        var startup = await h.Authority.OpenAsync(default);
        Assert.Equal(OutpostStartupState.Maintenance, startup.State); Assert.NotEmpty(startup.Reason!);
        var result = await new OutpostRestoreProcedure().ResumeAsync(h.Data.Root, request.Inspection.AnchorDirectory, request.RestoreId, default);
        if (result.Outcome != RestoreOutcome.Completed) h.KeepForDiagnostics = true;
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason + "; retained=" + h.Root + "; termination=" + termination);
        Assert.Equal(frozen, File.ReadAllBytes(path));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
        h.Db.ChangeTracker.Clear();
        Assert.Single(await h.Db.OutpostTransitions.Where(t => t.Kind == "BulkRestore").ToArrayAsync());
        Assert.Equal(prefix, OutpostRestoreEvidence.Encode(await h.Db.OutpostTransitions.AsNoTracking()
            .Where(t => t.Kind != "BulkRestore" && t.Kind != "RestoreCompletion").OrderBy(t => t.Id).ToArrayAsync()));
        var row = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var domain = await OutpostAuthority.ReadStoredChainAsync(h.Db, h.Files, row,
            new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), true, default);
        Assert.Equal(OutpostDomainFormat.V2, domain.Format); Assert.Equal("Additive", recorded.DomainStep!.Action);
        Assert.Equal(archiveDigest, SHA256.HashData(File.ReadAllBytes(archive)));
        var replay = await new OutpostRestoreProcedure().ResumeAsync(h.Data.Root, request.Inspection.AnchorDirectory, request.RestoreId, default);
        Assert.Equal(result.HeadId, replay.HeadId); Assert.Equal(result.HeadDigest, replay.HeadDigest);
    }
    // Same closed request wire as the existing owned worker; this fixture owns all referenced paths.
    private sealed record WorkerRequest(string Archive, string Data, string Anchor, string Id, string Installation,
        string Boundary, byte[] Confirmation, string Linux);
}
