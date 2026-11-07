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
                return Decode<RestoreMainPlan>(bytes);
            }
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
