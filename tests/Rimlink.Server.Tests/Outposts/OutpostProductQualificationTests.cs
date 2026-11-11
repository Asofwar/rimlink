using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostProductQualificationTests
{
    [Fact]
    public void Product_build_is_qualified_without_a_seal_and_reports_its_embedded_source_identity()
    {
        var observed = new OutpostProductQualification().Observe();
        Assert.Equal(OutpostRuntimeMode.Product, observed.Mode); Assert.Equal(OutpostQualificationState.Qualified, observed.State);
        Assert.True(observed.AutomatedPrerequisitesVerified);
        Assert.Equal(observed.SourceIdentity, Rimlink.Server.Application.GuildCapture.GuildCaptureReadiness.ProductSource);
        Assert.Matches("^[a-f0-9]{64}$", observed.SourceIdentity); Assert.False(observed.IsolationVerified);
        var assembly = typeof(OutpostFeature).Assembly;
        using var stream = assembly.GetManifestResourceStream("Rimlink.Outposts.SafetySources")!;
        using var reader = new StreamReader(stream); string manifest = reader.ReadToEnd();
        Assert.Contains("src/Rimlink.Server/Application/Outposts/OutpostReadiness.cs|", manifest);
        Assert.Contains("src/Rimlink.Client/Game/", manifest); Assert.Contains("SchemaProfiles/v1-crlf.json|", manifest);
        Assert.DoesNotContain("BuildQualification/qualification.json", manifest); Assert.DoesNotContain("/obj/", manifest);
    }

    [Theory]
    [InlineData("pending")][InlineData("source")][InlineData("missing")][InlineData("duplicate")]
    [InlineData("not-run")][InlineData("failed")][InlineData("hash")][InlineData("case-source")]
    public void Qualification_cannot_omit_or_substitute_required_acceptance_or_review(string damage)
    {
        string id = new string('a', 64);
        var evidence = OutpostProductQualification.AcceptanceCases.Select(name => new OutpostQualificationEvidence(name, id, "PASS", new string('b', 64))).ToArray();
        var artifact = new OutpostQualificationArtifact(1, "Qualified", id, evidence);
        Assert.True(OutpostProductQualification.IsComplete(artifact, id, OutpostProductQualification.AcceptanceCases));
        artifact = damage switch {
            "pending" => artifact with { State = "Pending" }, "source" => artifact with { SourceIdentity = new string('c', 64) },
            "missing" => artifact with { Evidence = evidence[..^1] }, "duplicate" => artifact with { Evidence = [.. evidence[..^1], evidence[0]] },
            "not-run" => artifact with { Evidence = [.. evidence[..^1], evidence[^1] with { Outcome = "NOT RUN" }] },
            "failed" => artifact with { Evidence = [.. evidence[..^1], evidence[^1] with { Outcome = "FAIL" }] },
            "hash" => artifact with { Evidence = [.. evidence[..^1], evidence[^1] with { EvidenceSha256 = "" }] },
            _ => artifact with { Evidence = [.. evidence[..^1], evidence[^1] with { SourceIdentity = new string('c', 64) }] }
        };
        Assert.False(OutpostProductQualification.IsComplete(artifact, id, OutpostProductQualification.AcceptanceCases));
    }
}
