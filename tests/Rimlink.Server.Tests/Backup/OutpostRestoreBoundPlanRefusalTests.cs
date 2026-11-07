using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreBoundPlanRefusalTests
{
    [Theory]
    [InlineData("AfterSchemaMigration", false)]
    [InlineData("AfterSchemaMigration", true)]
    [InlineData("AfterMainDatabaseCommit", false)]
    [InlineData("AfterMainDatabaseCommit", true)]
    [InlineData("AfterCompletionCommitted", false)]
    [InlineData("AfterCompletionCommitted", true)]
    public async Task A_well_formed_changed_executor_cannot_replace_the_bound_plan_after_ddl_or_R(string stop, bool backout)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await OutpostRestoreSchemaMigrationTests.PreAdminCut(f);
        var procedure = new OutpostRestoreProcedure(); var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
        procedure.Checkpoint = p => { if (p == stop) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
        string anchor = Path.GetDirectoryName(h.Anchor.DatabasePath)!;
        string path = Path.Combine(anchor, "restores", request.RestoreId, "main.json");
        var plan = OutpostRestoreSchemaMigrationTests.ReadPlan(f, request.RestoreId); var step = plan.DomainStep!;
        plan = plan with { DomainStep = step with { Executor = step.Executor! with
        { ArtifactSha256 = SHA256.HashData([42]), ModuleMvid = Guid.NewGuid().ToString("D") } } };
        File.WriteAllBytes(path, OutpostRestoreEvidence.Encode(plan));
        string before = await f.Cut();
        OutpostRestoreSchemaMigration.ExecutionProbe.Value = operation => throw new InvalidOperationException("must refuse bound P before execution: " + operation);
        try
        {
            RestoreResult result;
            if (backout)
            {
                var selected = procedure.InspectBackout(h.Data.Root, anchor, request.RestoreId);
                result = await new OutpostRestoreProcedure().BackoutAsync(selected with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(selected) }, default);
            }
            else result = await new OutpostRestoreProcedure().ResumeAsync(h.Data.Root, anchor, request.RestoreId, default);
            Assert.Equal(RestoreOutcome.Maintenance, result.Outcome);
            Assert.Equal(before, await f.Cut()); Assert.Equal(OutpostRestoreEvidence.Encode(plan), File.ReadAllBytes(path));
        }
        finally { OutpostRestoreSchemaMigration.ExecutionProbe.Value = null; }
    }

    [Theory]
    [InlineData("missing-plan")]
    [InlineData("unbound-post")]
    [InlineData("installed-verified-post")]
    public async Task Missing_P_or_an_unlicensed_phase_marker_pair_cannot_authorize_a_post_schema_resume(string damage)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await OutpostRestoreSchemaMigrationTests.PreAdminCut(f);
        var procedure = new OutpostRestoreProcedure(); var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
        procedure.Checkpoint = p => { if (p == "AfterSchemaMigration") throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
        string anchor = Path.GetDirectoryName(h.Anchor.DatabasePath)!, root = Path.Combine(anchor, "restores", request.RestoreId);
        var plan = OutpostRestoreSchemaMigrationTests.ReadPlan(f, request.RestoreId);
        if (damage == "missing-plan") File.Delete(Path.Combine(root, "main.json"));
        else if (damage == "unbound-post")
        {
            using var held = await h.Anchor.AcquireAsync(default); var record = h.Anchor.Read(held)!;
            h.Anchor.WriteDurable(held, record with { Restore = record.Restore! with { BackupDigest = ByteString.CopyFrom(plan.JournalDigest) } }, default);
        }
        else
        {
            string journalPath = Path.Combine(root, "journal.json");
            var journal = System.Text.Json.JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(journalPath), OutpostBackupManifest.Json)!;
            File.WriteAllBytes(journalPath, OutpostRestoreJournalCodec.Encode(journal with { Phase = RestorePhase.InstalledVerified }));
        }
        string before = await f.Cut();
        OutpostRestoreSchemaMigration.ExecutionProbe.Value = operation => throw new InvalidOperationException("must not repair the missing proof: " + operation);
        try
        {
            var result = await new OutpostRestoreProcedure().ResumeAsync(h.Data.Root, anchor, request.RestoreId, default);
            Assert.Equal(RestoreOutcome.Maintenance, result.Outcome); Assert.Equal(before, await f.Cut());
            Assert.Empty(await h.Db.OutpostTransitions.Where(t => t.Id == request.RestoreId).ToArrayAsync());
        }
        finally { OutpostRestoreSchemaMigration.ExecutionProbe.Value = null; }
    }
    private sealed class Stop : Exception;
}
