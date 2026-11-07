using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestorePlanCodecTests
{
    [Theory]
    [InlineData("migration")]
    [InlineData("executor")]
    public async Task Frozen_NoDdl_step_cannot_accept_additive_witness_fields_even_when_null(string field)
    {
        var (f, request, anchor) = await OutpostRestoreResumeTests.Interrupt("AfterMainPrepared");
        using (f)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(anchor, "restores", request.RestoreId, "main.json"));
            var json = JsonNode.Parse(bytes)!.AsObject(); var step = json["domainStep"]!.AsObject();
            Assert.Equal(1, step["version"]!.GetValue<int>()); step.Add(field, null);
            Assert.Throws<OutpostEvidenceUnavailableException>(() => OutpostRestoreEvidence.DecodePlan(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
        }
    }

    [Theory]
    [InlineData("domainStep")]
    [InlineData("cutManifest")]
    [InlineData("new-cut-envelope")]
    public async Task Historical_R1_codec_does_not_accept_fields_or_cut_shapes_from_R2(string damage)
    {
        var (f, request, anchor) = await OutpostRestoreResumeTests.Interrupt("AfterMainPrepared");
        using (f)
        {
            var current = OutpostRestoreEvidence.Decode<RestoreMainPlan>(File.ReadAllBytes(Path.Combine(anchor, "restores", request.RestoreId, "main.json")));
            var cut = damage == "new-cut-envelope" ? current.Cut : current.Cut with { Version = 1, Domain = null };
            byte[] cutDigest = SHA256.HashData(cut.Encode());
            var selected = current.Journal.Selection with { ManifestDigest = cutDigest };
            var journal = current.Journal with { Selection = selected, ManifestDigest = cutDigest, SelectionDigest = OutpostRestoreJournalCodec.Digest(selected) };
            var plan = current with { Version = 1, Journal = journal, JournalDigest = SHA256.HashData(OutpostRestoreEvidence.Encode(journal)),
                Cut = cut, CutManifest = null, DomainStep = null };
            var root = new OutpostTransition
            {
                Id = selected.RestoreId, WorldId = cut.Authority.WorldId, RestoreBoundary = selected.TargetBoundary,
                Kind = "BulkRestore", Actor = "offline-restore", OldHeadId = cut.Authority.HeadId,
                PredecessorId = cut.Authority.TransitionId, NextHeadId = plan.NextHeadId,
                CreatedAt = cut.CutUtc, Intent = OutpostRestoreEvidence.Encode(plan)
            };
            // A codec-only fixture is not permission to replace the actual retained intent or source cut.
            if (damage != "new-cut-envelope")
            {
                Assert.Equal(1, OutpostRestoreEvidence.Plan(root).Version);
                var json = JsonNode.Parse(root.Intent)!.AsObject(); json.Add(damage, null);
                root.Intent = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
            }
            string before = await f.Cut(); byte[] external = File.ReadAllBytes(f.H.F.H.H.Anchor.DatabasePath);
            Assert.Throws<OutpostEvidenceUnavailableException>(() => OutpostRestoreEvidence.Plan(root));
            Assert.Equal(before, await f.Cut()); Assert.Equal(external, File.ReadAllBytes(f.H.F.H.H.Anchor.DatabasePath));
        }
    }
}
