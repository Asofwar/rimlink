using System.Text.Json;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Application.GuildCapture;

internal static class GuildCaptureReadiness
{
    internal static readonly string[] AcceptanceCases =
    [
        "HISTORY", "HOME", "SLICE", "RESTORE", "runtime-fault", "runtime-performance",
        "guild-admin-parity", "guild-release-package", "astra-whole-branch-review", "review-fixes-verified",
    ];

    // No separate guild seal either: the product source is this build's own source identity.
    internal static string? ProductSource => OutpostProductQualification.SourceIdentity;

    internal static bool HasProductQualification(OutpostBuildQualification common, string? productSource) =>
        common is { Mode: OutpostRuntimeMode.Product, State: OutpostQualificationState.Qualified,
            AutomatedPrerequisitesVerified: true, IsolationVerified: false }
        && productSource is not null && productSource == common.SourceIdentity;

    internal static void RequireProduct(OutpostReadinessDecision common, GuildVerifiedCut? cut,
        bool enabled, string? productSource, GuildRuntimeUse use)
    {
        if (!enabled || !HasProductQualification(common.BuildQualification, productSource)
            || cut is not { Acceptance: null } || !Enum.IsDefined(use)
            || common is not { Mode: OutpostRuntimeMode.Product, DomainFormat: OutpostDomainFormat.V3,
                CurrentSchemaVerified: true, CanResolveCustody: true, CanRunDomainWriters: true,
                Startup.State: OutpostStartupState.Ready })
            throw new OutpostEvidenceUnavailableException("guild-readiness-unqualified");
    }
}
