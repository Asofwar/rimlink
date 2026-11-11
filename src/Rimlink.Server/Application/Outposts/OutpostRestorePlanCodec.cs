using System.Text;
using System.Text.Json;
using Rimlink.Server.Backup;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostRestoreEvidence
{
    // The old shape is closed independently: adding fields to R2 cannot widen an immutable R1 decoder.
    private sealed record LegacyRestoreMainPlan(int Version, RestoreIntent Journal, byte[] JournalDigest, OutpostBackupManifest Cut,
        string NextHeadId, string SourceDirectory, RestorePlayerMap[] Players, RestoreOutpostMap[] Outposts,
        RestoreCustodyMap[] Custody, RestoreParcelMap[] Parcels);

    private sealed record LegacyNoDdlStep(int Version, OutpostDomainFormat From, OutpostDomainFormat To, string Action,
        OutpostSchemaBinding Source, OutpostSchemaBinding Target);

    private sealed record LegacyRestoreMainPlanV2(int Version, RestoreIntent Journal, byte[] JournalDigest, OutpostBackupManifest Cut,
        string NextHeadId, string SourceDirectory, RestorePlayerMap[] Players, RestoreOutpostMap[] Outposts,
        RestoreCustodyMap[] Custody, RestoreParcelMap[] Parcels, RestoreDomainStep? DomainStep = null, byte[]? CutManifest = null);

    private sealed record LegacyRestoreCompletionPlan(int Version, string RestoreId, byte[] RootDigest, ClockSample Cut,
        RestoreCheckpointMap[] Checkpoints, string? PreviousCompletionId = null);

    internal static RestoreCompletionPlan ReadCompletion(byte[] bytes)
    {
        if (bytes.Length is 0 or > OutpostBackupManifest.MaximumBytes) throw Invalid("restore-evidence-limit");
        using var json = JsonSerializer.Deserialize<JsonDocument>(bytes, OutpostBackupManifest.Json) ?? throw Invalid("restore-completion-invalid");
        var versions = json.RootElement.EnumerateObject().Where(p => p.Name.Equals("version", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (versions.Length != 1 || !versions[0].Value.TryGetInt32(out int version)) throw Invalid("restore-completion-invalid");
        if (version == 1)
        {
            var legacy = Decode<LegacyRestoreCompletionPlan>(bytes);
            return new(legacy.Version, legacy.RestoreId, legacy.RootDigest, legacy.Cut, legacy.Checkpoints, legacy.PreviousCompletionId);
        }
        if (version != 2) throw Invalid("restore-completion-invalid");
        return Decode<RestoreCompletionPlan>(bytes);
    }

    internal static RestoreMainPlan DecodePlan(byte[] bytes)
    {
        if (bytes.Length is 0 or > OutpostBackupManifest.MaximumBytes) throw Invalid("restore-evidence-limit");
        try
        {
            using var document = JsonSerializer.Deserialize<JsonDocument>(bytes, OutpostBackupManifest.Json)
                ?? throw Invalid("restore-main-plan-codec-invalid");
            var versions = document.RootElement.EnumerateObject().Where(p => p.Name.Equals("version", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (versions.Length != 1 || !versions[0].Value.TryGetInt32(out int version)) throw Invalid("restore-main-plan-codec-invalid");
            if (version == 2)
            {
                var step = document.RootElement.EnumerateObject().Single(p => p.Name.Equals("domainStep", StringComparison.OrdinalIgnoreCase)).Value;
                var stepVersion = step.EnumerateObject().Where(p => p.Name.Equals("version", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (stepVersion.Length != 1 || !stepVersion[0].Value.TryGetInt32(out int number)) throw Invalid("restore-main-plan-codec-invalid");
                if (number == 1) _ = Decode<LegacyNoDdlStep>(Encoding.UTF8.GetBytes(step.GetRawText()));
                var old = Decode<LegacyRestoreMainPlanV2>(bytes);
                return new(old.Version, old.Journal, old.JournalDigest, old.Cut, old.NextHeadId, old.SourceDirectory,
                    old.Players, old.Outposts, old.Custody, old.Parcels, old.DomainStep, old.CutManifest);
            }
            if (version == 3) return Decode<RestoreMainPlan>(bytes);
            if (version != 1) throw Invalid("restore-main-plan-codec-unsupported");
            var legacy = Decode<LegacyRestoreMainPlan>(bytes);
            var cutFields = document.RootElement.EnumerateObject().Where(p => p.Name.Equals("cut", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (cutFields.Length != 1) throw Invalid("restore-main-plan-codec-invalid");
            var cut = OutpostBackupManifest.Decode(Encoding.UTF8.GetBytes(cutFields[0].Value.GetRawText()));
            if (cut.Version != 1 || !Same(cut, legacy.Cut)) throw Invalid("restore-main-plan-codec-unsupported");
            return new(legacy.Version, legacy.Journal, legacy.JournalDigest, legacy.Cut, legacy.NextHeadId,
                legacy.SourceDirectory, legacy.Players, legacy.Outposts, legacy.Custody, legacy.Parcels);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            throw Invalid("restore-main-plan-codec-invalid");
        }
    }
}
