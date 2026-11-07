using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreDomainFormatTests
{
    [Theory]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("action")]
    [InlineData("target")]
    [InlineData("raw-manifest")]
    [InlineData("downgrade")]
    public async Task Changed_R2_domain_step_or_original_manifest_never_commits_or_clears_the_external_fence(string damage)
    {
        var (f, request, anchor) = await OutpostRestoreResumeTests.Interrupt("AfterMainPrepared");
        using (f)
        {
            var h = f.H.F.H.H; string path = Path.Combine(anchor, "restores", request.RestoreId, "main.json");
            var plan = OutpostRestoreEvidence.Decode<RestoreMainPlan>(File.ReadAllBytes(path));
            Assert.Equal(2, plan.Version); Assert.NotNull(plan.DomainStep); Assert.NotNull(plan.CutManifest);
            plan = damage switch
            {
                "from" => plan with { DomainStep = plan.DomainStep! with { From = OutpostDomainFormat.V2 } },
                "to" => plan with { DomainStep = plan.DomainStep! with { To = OutpostDomainFormat.V1 } },
                "action" => plan with { DomainStep = plan.DomainStep! with { Action = "Additive" } },
                "target" => plan with { DomainStep = plan.DomainStep! with { Target = plan.DomainStep.Target with { Digest = new byte[32] } } },
                "raw-manifest" => plan with { CutManifest = [.. plan.CutManifest!, (byte)' '] },
                "downgrade" => plan with { Version = 1 },
                _ => throw new InvalidOperationException("unknown-test-damage")
            };
            File.WriteAllBytes(path, OutpostRestoreEvidence.Encode(plan));
            string before = await f.Cut(); byte[] external = File.ReadAllBytes(h.Anchor.DatabasePath);
            var result = await new OutpostRestoreProcedure().ResumeAsync(h.Data.Root, anchor, request.RestoreId, default);
            Assert.Equal(RestoreOutcome.Maintenance, result.Outcome); Assert.NotEmpty(result.Reason);
            Assert.Equal(before, await f.Cut()); Assert.Equal(external, File.ReadAllBytes(h.Anchor.DatabasePath));
            h.Db.ChangeTracker.Clear(); Assert.Empty(await h.Db.OutpostTransitions.Where(t => t.Id == request.RestoreId).ToArrayAsync());
            Assert.Equal(request.Inspection.Manifest.Authority.HeadId, (await h.Db.OutpostAuthority.SingleAsync()).HeadId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_restore_preserves_original_C_and_commits_direct_R_v2_with_exact_no_ddl(bool sourceV2)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        if (sourceV2)
        {
            var command = await OutpostAdminActivationTests.CommandForCurrent(h);
            await OutpostAdminActivationTests.Service(h).ActivateDomainFormatAsync(command.Intent.Actor, command, default);
        }
        string archive = await OutpostBackupTests.Create(f);
        var entries = OutpostBackupTests.Read(archive);
        // Legal JSON whitespace must remain physical source evidence, not be reserialized by the current encoder.
        byte[] originalManifest = Encoding.UTF8.GetBytes(" \n" + Encoding.UTF8.GetString(entries[OutpostBackupManifest.FileName]) + "\n");
        entries[OutpostBackupManifest.FileName] = originalManifest;
        archive = OutpostBackupFormatInspectionTests.Write(h.Root, entries);
        byte[] archiveDigest = SHA256.HashData(File.ReadAllBytes(archive));
        byte[] prefix = OutpostRestoreEvidence.Encode(await h.Db.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync());
        var procedure = new OutpostRestoreProcedure(); var request = await OutpostRestoreTests.Request(f, procedure, archive);
        f.Host.Dispose();
        var result = await procedure.ExecuteAsync(request, default);
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
        h.Db.ChangeTracker.Clear();
        var root = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "BulkRestore");
        using var document = JsonDocument.Parse(root.Intent); var plan = document.RootElement;
        Assert.Equal(2, plan.GetProperty("version").GetInt32());
        var step = plan.GetProperty("domainStep"); Assert.Equal(1, step.GetProperty("version").GetInt32());
        Assert.Equal(sourceV2 ? 2 : 1, step.GetProperty("from").GetInt32()); Assert.Equal(2, step.GetProperty("to").GetInt32());
        Assert.Equal("NoDdl", step.GetProperty("action").GetString());
        Assert.Equal(step.GetProperty("source").GetRawText(), step.GetProperty("target").GetRawText());
        Assert.Equal(originalManifest, plan.GetProperty("cutManifest").GetBytesFromBase64());
        Assert.Equal(originalManifest, File.ReadAllBytes(Path.Combine(h.Data.Root, plan.GetProperty("sourceDirectory").GetString()!, OutpostBackupManifest.FileName)));
        Assert.Equal(request.Inspection.Manifest.Authority.HeadId, root.OldHeadId);
        Assert.Equal(prefix, OutpostRestoreEvidence.Encode(await h.Db.OutpostTransitions.AsNoTracking().Where(t => t.Kind != "BulkRestore" && t.Kind != "RestoreCompletion").OrderBy(t => t.Id).ToArrayAsync()));
        var row = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var observed = await OutpostAuthority.ReadStoredChainAsync(h.Db, h.Files, row, new(row.HeadId, Google.Protobuf.ByteString.CopyFrom(row.HeadDigest)), true, default);
        Assert.Equal(OutpostDomainFormat.V2, observed.Format);
        Assert.Equal(sourceV2 ? 1 : 0, await h.Db.OutpostAdminCommands.CountAsync());
        Assert.Equal(sourceV2 ? 1 : 0, await h.Db.AdminActions.CountAsync(a => a.Action == "outposts.ActivateDomainFormat"));
        Assert.Equal(archiveDigest, SHA256.HashData(File.ReadAllBytes(archive)));
    }
}
