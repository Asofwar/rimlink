namespace Rimlink.Server.Application.Outposts;

public enum OutpostRuntimeMode { Product = 1, IsolatedAcceptance = 2 }
public enum OutpostIntegrationState { Unknown = 0, Implemented = 1, Unavailable = 2 }
public enum OutpostQualificationState { Pending = 0, Qualified = 1 }
public sealed record OutpostIntegration(string Id, int Revision, OutpostIntegrationState State);
public sealed record OutpostBuildQualification(OutpostRuntimeMode Mode, OutpostQualificationState State, string SourceIdentity,
    bool AutomatedPrerequisitesVerified, bool IsolationVerified);
public interface IOutpostBuildQualification
{
    OutpostBuildQualification Observe();
}
public sealed record OutpostReadinessInputs(bool ConfiguredEnabled, OutpostIntegration[] Integrations,
    OutpostBuildQualification Build, OutpostStartupResult Startup, OutpostDomainFormat? DomainFormat,
    bool CurrentSchemaVerified, bool? HasHistory);
public sealed record OutpostReadinessDecision(OutpostRuntimeMode Mode, bool ConfiguredEnabled, OutpostIntegration[] IntegrationStates,
    OutpostBuildQualification BuildQualification, OutpostStartupResult Startup, OutpostDomainFormat? DomainFormat,
    bool CurrentSchemaVerified, bool AdvertiseCapability, bool CanResolveCustody, bool HistoryProtectionRequired,
    bool CanRunDomainWriters, string[] Blockers, bool CanBindSuccessorWorld);

public static class OutpostReadiness
{
    public static readonly IReadOnlyList<string> RequiredIntegrations = Array.AsReadOnly(new[] {
        "authority-storage", "exact-metadata-registry-receipts", "admission-cancellation", "delivery-wealth",
        "ordinary-sources-returns-fences", "live-raid-coop-ownership", "archive-reset-rollback",
        "coherent-backup-restore-provenance", "game-core-callbacks", "audited-admin-parity" });
    public const int ContractRevision = 1;

    public static OutpostReadinessDecision Evaluate(OutpostReadinessInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var blockers = new List<string>();
        var integrations = new List<OutpostIntegration>();
        foreach (string id in RequiredIntegrations)
        {
            var matches = (inputs.Integrations ?? []).Where(i => i is not null && i.Id == id).ToArray();
            var observed = matches.Length == 1 ? matches[0] : new(id, 0, OutpostIntegrationState.Unknown);
            integrations.Add(observed);
            if (observed.State != OutpostIntegrationState.Implemented || observed.Revision != ContractRevision)
                blockers.Add("integration-unavailable:" + id);
        }
        if ((inputs.Integrations ?? []).Any(i => i is null || !RequiredIntegrations.Contains(i.Id)))
            blockers.Add("integration-inventory-unsupported");
        var build = inputs.Build;
        bool identity = build.SourceIdentity is { Length: 64 } && build.SourceIdentity.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (!identity || !Enum.IsDefined(build.Mode) || !Enum.IsDefined(build.State)) blockers.Add("build-qualification-unavailable");
        if (!build.AutomatedPrerequisitesVerified) blockers.Add("automated-prerequisites-unverified");
        if (build.Mode == OutpostRuntimeMode.Product && build.State != OutpostQualificationState.Qualified)
            blockers.Add("product-acceptance-pending");
        if (build.Mode == OutpostRuntimeMode.IsolatedAcceptance && !build.IsolationVerified)
            blockers.Add("acceptance-isolation-unverified");
        bool safetyQualified = blockers.Count == 0;
        var startup = inputs.Startup;
        if (startup.State == OutpostStartupState.Ready && inputs.DomainFormat == OutpostDomainFormat.V1 && inputs.CurrentSchemaVerified)
            startup = new(OutpostStartupState.FormatActivationRequired, "outpost-domain-format-activation-required");
        if (startup.State != OutpostStartupState.Ready) blockers.Add("startup-not-ready");
        if (inputs.DomainFormat is not(OutpostDomainFormat.V2 or OutpostDomainFormat.V3)) blockers.Add(inputs.DomainFormat == OutpostDomainFormat.V1
            ? "domain-format-activation-required" : "domain-format-unverified");
        if (!inputs.CurrentSchemaVerified) blockers.Add("current-schema-unverified");
        if (inputs.HasHistory is null) blockers.Add("history-observation-unavailable");
        if (inputs.DomainFormat==OutpostDomainFormat.V3 && inputs.HasHistory!=true) blockers.Add("guild-domain-history-unverified");
        bool resolve = blockers.Count == 0;
        bool bindSuccessor = safetyQualified && startup.State == OutpostStartupState.NoActiveWorld
            && inputs.DomainFormat is (OutpostDomainFormat.V2 or OutpostDomainFormat.V3) && inputs.CurrentSchemaVerified && inputs.HasHistory == true;
        if (!inputs.ConfiguredEnabled) blockers.Add("disabled-by-configuration");
        bool writers = inputs.DomainFormat==OutpostDomainFormat.V3?resolve:inputs.HasHistory == false && startup.State is not (OutpostStartupState.Maintenance or OutpostStartupState.NoActiveWorld)
            || inputs.HasHistory == true && resolve;
        return new(build.Mode, inputs.ConfiguredEnabled, integrations.ToArray(), build, startup, inputs.DomainFormat, inputs.CurrentSchemaVerified,
            resolve && inputs.ConfiguredEnabled, resolve, inputs.HasHistory != false, writers, blockers.ToArray(), bindSuccessor);
    }
}
