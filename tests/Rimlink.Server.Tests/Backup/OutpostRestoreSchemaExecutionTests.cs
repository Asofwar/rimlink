using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreSchemaExecutionTests
{
    [Fact]
    public async Task Fresh_additive_restore_observes_executor_recipe_and_exactly_one_migrator_call()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostRestoreSchemaMigrationTests.PreAdminCut(f);
        var procedure = new OutpostRestoreProcedure(); var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
        var calls = new List<string>(); OutpostRestoreSchemaMigration.ExecutionProbe.Value = calls.Add;
        try
        {
            var result = await procedure.ExecuteAsync(request, default);
            Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
            Assert.Contains("artifact", calls); Assert.Contains("recipe", calls); Assert.Equal(1, calls.Count(c => c == "migrator"));
            var step = OutpostRestoreSchemaMigrationTests.ReadPlan(f, request.RestoreId).DomainStep!;
            string framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            string qualification = framework switch
            {
                ".NET 10.0.13" => "rimlink-outposts-schema-20261105-0520",
                ".NET 10.0.14" => "rimlink-outposts-schema-20261107-runtime14",
                _ => throw new InvalidOperationException("unqualified test executor: " + framework)
            };
            Assert.Equal(framework, step.Executor!.Framework);
            Assert.Equal(qualification, step.Migration!.QualificationId);
            Assert.Equal(qualification + "/provider-v1", step.Migration.ProviderProfile);
        }
        finally { OutpostRestoreSchemaMigration.ExecutionProbe.Value = null; }
    }

    [Fact]
    public async Task Bound_post_schema_resume_and_real_nested_history_never_consult_an_executor_generator_or_migrator()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await OutpostRestoreSchemaMigrationTests.PreAdminCut(f);
        var procedure = new OutpostRestoreProcedure(); var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
        procedure.Checkpoint = p => { if (p == "AfterSchemaMigration") throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
        Assert.True((await OutpostDomainSchema.ObserveAsync(h.Db, default)).IsCurrent);
        var frozen = OutpostRestoreSchemaMigrationTests.ReadPlan(f, request.RestoreId);
        OutpostRestoreSchemaMigration.ExecutionProbe.Value = operation => throw new InvalidOperationException("unexpected schema execution in proven post state: " + operation);
        try
        {
            var result = await new OutpostRestoreProcedure().ResumeAsync(h.Data.Root, Path.GetDirectoryName(h.Anchor.DatabasePath)!, request.RestoreId, default);
            Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
            h.Db.ChangeTracker.Clear();
            await new OutpostSnapshotGuard(h.Db, h.Files).ValidateStoredEvidenceCoreAsync(default);
            Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
            Assert.Equal(OutpostRestoreEvidence.Encode(frozen), OutpostRestoreEvidence.Encode(OutpostRestoreSchemaMigrationTests.ReadPlan(f, request.RestoreId)));
            await h.Db.Database.CloseConnectionAsync();
            string nextArchive = Path.Combine(h.Data.Backups, "nested-after-additive.tar.zst");
            await StoppedBackup.CreateAsync(h.Data.Root, nextArchive, new Rimlink.Server.Hosting.OutpostOptions
            {
                AnchorDirectory = Path.GetDirectoryName(h.Anchor.DatabasePath)!, StorageContract = "sqlite-local-v1",
                QualifiedLinuxFileSystem = Rimlink.Server.Tests.Support.LocalTestStorage.LinuxQualification(h.Data.Root)
            }, default);
            var next = new OutpostRestoreProcedure(); var nextRequest = await OutpostRestoreTests.Request(f, next, nextArchive);
            var nested = await next.ExecuteAsync(nextRequest, default); Assert.True(nested.Outcome == RestoreOutcome.Completed, nested.Reason);
            h.Db.ChangeTracker.Clear();
            Assert.Equal(2, await h.Db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
            await new OutpostSnapshotGuard(h.Db, h.Files).ValidateStoredEvidenceCoreAsync(default);
        }
        finally { OutpostRestoreSchemaMigration.ExecutionProbe.Value = null; }
    }

    private sealed class Stop : Exception;
}
