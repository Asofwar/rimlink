using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Application.GuildCapture;

internal enum GuildAcceptanceScope { Home=1,Slice=2,Restore=3 }
internal enum GuildRuntimeUse { Transport=1,Setup=2,Recovery=3,Simulation=4,Economy=5 }
internal sealed record GuildRuntimeGrant(string StandId,string BootstrapDigest,string SourceIdentity,string WorldId,string Boundary,GuildAcceptanceScope Scope,bool ClockSynchronized=true);
internal interface IGuildRuntimeAdmission
{
    string? StandId => null;
    IGuildAcceptanceClockControl? ClockControl => null;
    string? QualifiedProductSource => null;
    GuildRuntimeGrant? Observe();
}

// No ordinary config or environment value can supply an acceptance grant or qualify Product.
internal sealed class GuildProductRuntimeAdmission:IGuildRuntimeAdmission
{
    public string? QualifiedProductSource => GuildCaptureReadiness.ProductSource;
    public GuildRuntimeGrant? Observe()=>null;
}

internal static class GuildRuntimeAdmission
{
    internal static void Require(OutpostReadinessDecision common,GuildVerifiedCut? cut,GuildRuntimeGrant? grant,GuildRuntimeUse use)
    {
        if(grant is null || !grant.ClockSynchronized || cut is null || !Enum.IsDefined(use) || !Enum.IsDefined(grant.Scope) || !Uuid(grant.StandId) || !Hash(grant.BootstrapDigest) || !Hash(grant.SourceIdentity)
            || common.Mode!=OutpostRuntimeMode.IsolatedAcceptance || common.BuildQualification.Mode!=OutpostRuntimeMode.IsolatedAcceptance
            || !common.BuildQualification.AutomatedPrerequisitesVerified || !common.BuildQualification.IsolationVerified || !common.CanResolveCustody || !common.CanRunDomainWriters
            || common.Startup.State!=OutpostStartupState.Ready || common.DomainFormat!=OutpostDomainFormat.V3 || !common.CurrentSchemaVerified
            || grant.SourceIdentity!=common.BuildQualification.SourceIdentity || grant.WorldId!=cut.WorldId || grant.Boundary!=cut.Boundary
            || cut.Acceptance is null || !Matches(cut.Acceptance,grant)
            || grant.Scope==GuildAcceptanceScope.Home && use is GuildRuntimeUse.Simulation or GuildRuntimeUse.Economy)
            throw new OutpostEvidenceUnavailableException("guild-readiness-unqualified");
    }
    internal static void RequireProvenance(OutpostBuildQualification common,GuildAcceptanceProvenance identity,GuildRuntimeGrant? grant)
    {
        GuildAuthorityEvidence.ValidateAcceptanceIdentity(identity);
        if(grant is null || common.Mode!=OutpostRuntimeMode.IsolatedAcceptance || !common.AutomatedPrerequisitesVerified || !common.IsolationVerified
            || common.SourceIdentity!=identity.SourceIdentity || !Matches(identity,grant))throw new OutpostEvidenceUnavailableException("guild-acceptance-provenance-unavailable");
    }
    private static bool Matches(GuildAcceptanceProvenance identity,GuildRuntimeGrant grant)=>identity.Version==1 && identity.Mode=="GuildCaptureIsolatedAcceptanceV1"
        && identity.StandId==grant.StandId && identity.BootstrapDigest==grant.BootstrapDigest && identity.SourceIdentity==grant.SourceIdentity
        && identity.WorldId==grant.WorldId && identity.Boundary==grant.Boundary;
    internal static GuildRuntimeUse MutationUse(string kind)=>kind switch
    {
        "GuildResourceClaim" or "GuildBeginDelivery"=>GuildRuntimeUse.Economy,
        "GuildAttackerReady" or "GuildBattleStarted" or "GuildBattleApplied" or "GuildBattleControlReconciled"=>GuildRuntimeUse.Simulation,
        "GuildExecutorCleanup" or "GuildCampaignExpired" or GuildOperatorCancellation.Kind or GuildFreezeReceipt.Kind or GuildStartedAbortReceipt.Kind=>GuildRuntimeUse.Recovery,
        GuildAcceptanceClockRules.Kind or "GuildGovernance" or "GuildPlacement" or "GuildConsent" or "GuildProduction" or "GuildCampaignScheduled" or "GuildBattlePrepared" or "GuildBattleProfileApproved"
            or "GuildBattleProfileRevoked" or "GuildDefenderCommanderAssigned"=>GuildRuntimeUse.Setup,
        _=>throw new OutpostEvidenceUnavailableException("guild-runtime-mutation-unsupported")
    };
    private static bool Hash(string value)=>value is{Length:64} && value.All(c=>c is >='0' and <='9' or >='a' and <='f');
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
}
