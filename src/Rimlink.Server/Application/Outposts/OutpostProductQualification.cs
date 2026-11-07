using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rimlink.Server.Application.Outposts;

public sealed record OutpostQualificationEvidence(string Case, string SourceIdentity, string Outcome, string EvidenceSha256);
public sealed record OutpostQualificationArtifact(int Version, string State, string? SourceIdentity, OutpostQualificationEvidence[] Evidence);

internal sealed class OutpostProductQualification : IOutpostBuildQualification
{
    private static readonly Lazy<string> Identity = new(ReadSourceIdentity);
    public static string SourceIdentity => Identity.Value;
    internal static readonly string[] AutomatedCases = ["solution-build", "windows-full-suite", "linux-two-cpu-full-suite", "game-build", "bridge-build", "packaging-exclusion", "admin-parity", "wiki-verification"];
    internal static readonly string[] AcceptanceCases = [.. AutomatedCases, "G1", "G2", "G3", "G4", "G5", "G6", "G7", "astra-whole-branch-review", "review-fixes-verified"];

    public OutpostBuildQualification Observe()
    {
        using var stream = typeof(OutpostProductQualification).Assembly.GetManifestResourceStream("Rimlink.Outposts.Qualification")
            ?? throw new InvalidDataException("outpost-build-qualification-missing");
        var artifact = JsonSerializer.Deserialize<OutpostQualificationArtifact>(stream, Backup.OutpostBackupManifest.Json)
            ?? throw new InvalidDataException("outpost-build-qualification-invalid");
        bool qualified = IsComplete(artifact, SourceIdentity, AcceptanceCases);
        return new(OutpostRuntimeMode.Product, qualified ? OutpostQualificationState.Qualified : OutpostQualificationState.Pending,
            SourceIdentity, qualified, false);
    }

    internal static bool IsComplete(OutpostQualificationArtifact artifact, string sourceIdentity, IReadOnlyList<string> cases) =>
        artifact is { Version: 1, State: "Qualified", Evidence: not null } && artifact.SourceIdentity == sourceIdentity
        && artifact.Evidence.Length == cases.Count && artifact.Evidence.Select(e => e.Case).Distinct(StringComparer.Ordinal).Count() == cases.Count
        && cases.All(name => artifact.Evidence.Any(e => e.Case == name && e.SourceIdentity == sourceIdentity && e.Outcome == "PASS" && Hash(e.EvidenceSha256)));

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string ReadSourceIdentity()
    {
        using var stream = typeof(OutpostProductQualification).Assembly.GetManifestResourceStream("Rimlink.Outposts.SafetySources")
            ?? throw new InvalidDataException("outpost-build-source-identity-missing");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var entries = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
        if (entries.Length < 10 || entries.Distinct(StringComparer.Ordinal).Count() != entries.Length
            || entries.Any(line => line.Split('|') is not [var path, var hash] || path.StartsWith('/') || path.Contains(':')
                || path.Contains("../", StringComparison.Ordinal) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("outpost-build-source-identity-invalid");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries) + "\n")));
    }
}
