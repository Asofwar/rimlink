using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostBuildSealingTests
{
    [Theory]
    [InlineData("missing")][InlineData("failed")][InlineData("not-run")][InlineData("wrong-source")][InlineData("changed-file")]
    public void Incomplete_or_changed_evidence_cannot_seal_product(string defect)
    {
        using var f = new EvidenceFixture(); var checks = f.Checks;
        if (defect == "missing") checks = checks.Skip(1).ToArray();
        if (defect == "failed") checks[0] = checks[0] with { Outcome = "FAIL" };
        if (defect == "not-run") checks[0] = checks[0] with { Outcome = "NOT RUN" };
        if (defect == "wrong-source") checks[0] = checks[0] with { SourceIdentity = new string('0', 64) };
        if (defect == "changed-file") File.AppendAllText(checks[0].EvidenceFile, "changed");
        File.WriteAllText(f.Record, JsonSerializer.Serialize(new BuildEvidence(1, OutpostProductQualification.SourceIdentity, checks), AcceptanceStandFiles.Json));
        File.WriteAllText(f.Output, "original pending artifact");
        Assert.Throws<InvalidDataException>(() => BuildQualificationStep.Write("build-seal", f.Record, f.Output));
        Assert.Equal("original pending artifact", File.ReadAllText(f.Output));
    }

    [Fact]
    public void Synthetic_complete_candidate_stays_pending_until_explicit_build_seal()
    {
        using var f = new EvidenceFixture();
        File.WriteAllText(f.Record, JsonSerializer.Serialize(new BuildEvidence(1, OutpostProductQualification.SourceIdentity, f.Checks), AcceptanceStandFiles.Json));
        BuildQualificationStep.Write("build-candidate", f.Record, f.Output);
        Assert.Equal("Pending", AcceptanceStandFiles.Read<OutpostQualificationArtifact>(f.Output).State);
        BuildQualificationStep.Write("build-seal", f.Record, f.Output);
        Assert.Equal("Qualified", AcceptanceStandFiles.Read<OutpostQualificationArtifact>(f.Output).State);
    }

    [Fact]
    public void Guild_seal_requires_its_own_successful_restore_evidence()
    {
        using var f = new EvidenceFixture(guildCapture: true);
        var checks = f.Checks.Select(check => check.Case == "RESTORE" ? check with { Outcome = "NOT RUN" } : check).ToArray();
        File.WriteAllText(f.Record, JsonSerializer.Serialize(new BuildEvidence(1, OutpostProductQualification.SourceIdentity, checks), AcceptanceStandFiles.Json));
        Assert.Throws<InvalidDataException>(() => BuildQualificationStep.Write("build-seal", f.Record, f.Output, guildCapture: true));
        Assert.False(File.Exists(f.Output));

        File.WriteAllText(f.Record, JsonSerializer.Serialize(new BuildEvidence(1, OutpostProductQualification.SourceIdentity, f.Checks), AcceptanceStandFiles.Json));
        Assert.Throws<InvalidDataException>(() => BuildQualificationStep.Write("build-seal", f.Record, f.Output));
        BuildQualificationStep.Write("build-seal", f.Record, f.Output, guildCapture: true);
        Assert.Equal("Qualified", AcceptanceStandFiles.Read<OutpostQualificationArtifact>(f.Output).State);
    }
    private sealed class EvidenceFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rimlink-build-qualification-test-" + Guid.NewGuid().ToString("N"));
        internal string Record => Path.Combine(root, "evidence.json");
        internal string Output => Path.Combine(root, "artifact.json");
        internal AcceptanceCheck[] Checks { get; }
        internal EvidenceFixture(bool guildCapture = false)
        {
            Directory.CreateDirectory(root);
            var cases = guildCapture ? GuildCaptureReadiness.AcceptanceCases : OutpostProductQualification.AcceptanceCases;
            Checks = cases.Select((name, i) =>
            {
                string path = Path.Combine(root, i + ".log"); File.WriteAllText(path, "Synthetic unit-test input, not acceptance evidence: " + name);
                return new AcceptanceCheck(name, "PASS", OutpostProductQualification.SourceIdentity, path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            }).ToArray();
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
