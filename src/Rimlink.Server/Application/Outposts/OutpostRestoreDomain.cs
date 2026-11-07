using Rimlink.Server.Backup;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostRestoreEvidence
{
    internal static byte[] ManifestBytes(RestoreMainPlan plan) => plan.Version switch
    {
        1 when plan.CutManifest is null && plan.DomainStep is null => plan.Cut.Encode(),
        2 when plan.CutManifest is { Length: > 0 and <= OutpostBackupManifest.MaximumBytes } => plan.CutManifest.ToArray(),
        _ => throw Invalid("restore-main-domain-binding-invalid")
    };

    internal static void ValidateDomainStep(RestoreMainPlan plan)
    {
        if (plan.Version == 1)
        {
            if (plan.DomainStep is not null || plan.CutManifest is not null || plan.Cut.Version != 1 || plan.Cut.Domain is not null)
                throw Invalid("restore-main-domain-binding-invalid");
            return;
        }
        var step = plan.DomainStep;
        if (plan.Version != 2 || step is null || !Enum.IsDefined(step.From) || step.To != OutpostDomainFormat.V2
            || !Same(OutpostBackupManifest.Decode(ManifestBytes(plan)), plan.Cut)
            || plan.Cut.Version == 1 && step.From != OutpostDomainFormat.V1
            || plan.Cut.Domain is { } domain && (domain.Format != step.From || !Same(domain.Schema, step.Source)))
            throw Invalid("restore-main-domain-binding-invalid");
        ValidateRecordedDomainStep(step);
    }

    private static void ValidateRecordedDomainStep(RestoreDomainStep step)
    {
        if (step.Version == 2) { OutpostRestoreSchemaMigration.ValidateRecordedStep(step); return; }
        if (step.Version != 1 || step.Action != "NoDdl" || step.Migration is not null || step.Executor is not null
            || !OutpostDomainSchema.IsKnownBinding(step.Source, true) || !OutpostDomainSchema.IsKnownBinding(step.Target, true)
            || !Same(step.Source, step.Target)) throw Invalid("restore-main-domain-binding-invalid");
    }

    internal static void ValidateSourceDomain(RestoreDomainStep step, OutpostBackupManifest cut,
        OutpostAuthority.StoredDomainObservation observed, bool allowPostSchema = false)
    {
        ValidateRecordedDomainStep(step);
        if (observed.Format != step.From || !observed.Digest.Span.SequenceEqual(cut.DomainDigest))
            throw Invalid("restore-schema-source-domain-mismatch");
        var actual = new OutpostSchemaBinding(observed.Schema.ProfileId, observed.Schema.Digest.ToByteArray());
        if (Same(actual, step.Source)) { cut.ValidateDomain(observed); return; }
        if (!allowPostSchema || step.Action != "Additive" || !Same(actual, step.Target))
            throw Invalid("restore-schema-source-profile-mismatch");
        // The verified pre-R v1 chain plus exact current schema requires the new admin table to be empty.
        // Current R maps must never be compared to this old source digest after R has committed.
    }

    internal static OutpostDomainFormat TargetFormat(RestoreMainPlan plan)
    {
        ValidateDomainStep(plan);
        return plan.Version == 1 ? OutpostDomainFormat.V1 : plan.DomainStep!.To;
    }
}
