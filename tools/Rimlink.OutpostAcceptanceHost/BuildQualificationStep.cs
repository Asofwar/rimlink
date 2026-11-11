using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed record BuildEvidence(int Version, string SourceIdentity, AcceptanceCheck[] Checks);

// This command only emits an artifact for a subsequent build. No hosted endpoint can invoke it.
internal static class BuildQualificationStep
{
    internal static OutpostQualificationArtifact Evaluate(BuildEvidence record, bool guildCapture = false)
    {
        string identity = OutpostProductQualification.SourceIdentity;
        var cases = guildCapture ? GuildCaptureReadiness.AcceptanceCases : OutpostProductQualification.AcceptanceCases;
        if (record.Version != 1 || record.SourceIdentity != identity || record.Checks is null
            || record.Checks.Length > cases.Length
            || record.Checks.Select(c => c.Case).Distinct(StringComparer.Ordinal).Count() != record.Checks.Length
            || record.Checks.Any(c => !cases.Contains(c.Case, StringComparer.Ordinal)))
            throw new InvalidDataException("qualification-evidence-record-invalid");
        var evidence = new List<OutpostQualificationEvidence>();
        foreach (var check in record.Checks)
        {
            if (check.SourceIdentity != identity || check.Outcome is not ("PASS" or "FAIL" or "NOT RUN")
                || !Path.IsPathFullyQualified(check.EvidenceFile) || !File.Exists(check.EvidenceFile)
                || AcceptanceStandFiles.HashFile(check.EvidenceFile) != check.EvidenceSha256)
                throw new InvalidDataException("qualification-evidence-file-unverified:" + check.Case);
            evidence.Add(new(check.Case, identity, check.Outcome, check.EvidenceSha256));
        }
        var candidate = new OutpostQualificationArtifact(1, "Qualified", identity, evidence.ToArray());
        return OutpostProductQualification.IsComplete(candidate, identity, cases)
            ? candidate : candidate with { State = "Pending" };
    }

    internal static void Write(string action, string recordPath, string outputPath, bool guildCapture = false)
    {
        var artifact = Evaluate(AcceptanceStandFiles.Read<BuildEvidence>(Path.GetFullPath(recordPath)), guildCapture);
        if (action == "build-seal" && artifact.State != "Qualified") throw new InvalidDataException("qualification-incomplete-product-remains-pending");
        if (action is not ("build-candidate" or "build-seal")) throw new InvalidDataException("qualification-command-invalid");
        if (action == "build-candidate") artifact = artifact with { State = "Pending" };
        string output = Path.GetFullPath(outputPath);
        if (output.Equals(Path.GetFullPath(recordPath), AcceptancePaths.Comparison)) throw new InvalidDataException("qualification-output-overlaps-evidence");
        // One atomic build artifact replacement; never a server data/anchor/restore procedure.
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, artifact, AcceptanceStandFiles.Json); stream.Flush(flushToDisk: true); }
            File.Move(temporary, output, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
