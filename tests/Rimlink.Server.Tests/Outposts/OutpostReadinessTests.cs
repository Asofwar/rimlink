using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostReadinessTests
{
    [Fact]
    public void Pending_product_never_advertises_or_resolves_even_when_every_integration_and_configuration_is_ready()
    {
        var input = Ready() with { Build = Ready().Build with { State = OutpostQualificationState.Pending } };
        var decision = OutpostReadiness.Evaluate(input);
        Assert.False(decision.AdvertiseCapability); Assert.False(decision.CanResolveCustody);
        Assert.True(decision.HistoryProtectionRequired); Assert.False(decision.CanRunDomainWriters);
        Assert.Contains("product-acceptance-pending", decision.Blockers);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void Qualified_recovery_is_independent_of_new_gameplay_configuration(bool enabled)
    {
        var decision = OutpostReadiness.Evaluate(Ready() with { ConfiguredEnabled = enabled });
        Assert.Equal(enabled, decision.AdvertiseCapability); Assert.True(decision.CanResolveCustody);
        Assert.True(decision.CanRunDomainWriters); Assert.True(decision.HistoryProtectionRequired);
    }

    [Theory]
    [InlineData(OutpostIntegrationState.Unknown)][InlineData(OutpostIntegrationState.Unavailable)]
    public void Every_missing_unknown_wrong_revision_or_duplicate_integration_fails_closed(OutpostIntegrationState state)
    {
        foreach (string required in OutpostReadiness.RequiredIntegrations)
        {
            var input = Ready();
            foreach (var integrations in new[] {
                input.Integrations.Where(i => i.Id != required).ToArray(),
                input.Integrations.Select(i => i.Id == required ? i with { State = state } : i).ToArray(),
                input.Integrations.Select(i => i.Id == required ? i with { Revision = 999 } : i).ToArray(),
                input.Integrations.Append(input.Integrations.Single(i => i.Id == required)).ToArray() })
            {
                var decision = OutpostReadiness.Evaluate(input with { Integrations = integrations });
                Assert.False(decision.AdvertiseCapability); Assert.False(decision.CanResolveCustody);
                Assert.Contains("integration-unavailable:" + required, decision.Blockers);
            }
        }
    }

    [Theory]
    [InlineData(OutpostStartupState.Maintenance)][InlineData(OutpostStartupState.NoActiveWorld)]
    [InlineData(OutpostStartupState.RequiresInitialization)]
    public void Startup_restrictions_keep_history_fenced(OutpostStartupState state)
    {
        var result = OutpostReadiness.Evaluate(Ready() with { Startup = new(state) });
        Assert.False(result.CanResolveCustody); Assert.False(result.AdvertiseCapability); Assert.False(result.CanRunDomainWriters);
        Assert.True(result.HistoryProtectionRequired);
    }

    [Fact]
    public void Migrated_v1_is_activation_required_not_corruption_and_schema_presence_does_not_activate_it()
    {
        var result = OutpostReadiness.Evaluate(Ready() with { DomainFormat = OutpostDomainFormat.V1 });
        Assert.False(result.CanResolveCustody); Assert.False(result.AdvertiseCapability);
        Assert.Equal("outpost-domain-format-activation-required", result.Startup.Reason);
        Assert.Contains("domain-format-activation-required", result.Blockers);
        Assert.False(OutpostReadiness.Evaluate(Ready() with { CurrentSchemaVerified = false }).CanResolveCustody);
        Assert.False(OutpostReadiness.Evaluate(Ready() with { DomainFormat = null }).CanResolveCustody);
    }

    [Fact]
    public void Isolated_mode_waives_only_product_seal_and_requires_automated_and_current_isolation_checks()
    {
        var input = Ready();
        input = input with { Build = input.Build with { Mode = OutpostRuntimeMode.IsolatedAcceptance, State = OutpostQualificationState.Pending } };
        Assert.True(OutpostReadiness.Evaluate(input).AdvertiseCapability);
        Assert.False(OutpostReadiness.Evaluate(input with { Build = input.Build with { AutomatedPrerequisitesVerified = false } }).CanResolveCustody);
        Assert.False(OutpostReadiness.Evaluate(input with { Build = input.Build with { IsolationVerified = false } }).CanResolveCustody);
        Assert.False(OutpostReadiness.Evaluate(input with { Integrations = [] }).CanResolveCustody);
    }

    [Fact]
    public void Proven_unrelated_legacy_can_run_ordinary_writers_but_unknown_history_never_counts_as_empty()
    {
        var pending = Ready() with { Build = Ready().Build with { State = OutpostQualificationState.Pending } };
        Assert.True(OutpostReadiness.Evaluate(pending with { HasHistory = false }).CanRunDomainWriters);
        Assert.False(OutpostReadiness.Evaluate(pending with { HasHistory = null }).CanRunDomainWriters);
        Assert.True(OutpostReadiness.Evaluate(pending with { HasHistory = null }).HistoryProtectionRequired);
    }

    internal static OutpostReadinessInputs Ready() => new(true,
        OutpostReadiness.RequiredIntegrations.Select(id => new OutpostIntegration(id, OutpostReadiness.ContractRevision, OutpostIntegrationState.Implemented)).ToArray(),
        new(OutpostRuntimeMode.Product, OutpostQualificationState.Qualified, new string('a', 64), true, true),
        new(OutpostStartupState.Ready), OutpostDomainFormat.V2, true, true);

    [Fact]
    public void Qualified_successor_gate_preserves_real_NoActiveWorld_without_opening_ordinary_work()
    {
        var input = Ready() with { Startup = new(OutpostStartupState.NoActiveWorld), ConfiguredEnabled = false };
        var decision = OutpostReadiness.Evaluate(input);
        Assert.True(decision.CanBindSuccessorWorld); Assert.False(decision.CanResolveCustody);
        Assert.False(decision.CanRunDomainWriters); Assert.False(decision.AdvertiseCapability);
        Assert.Equal(OutpostStartupState.NoActiveWorld, decision.Startup.State);
        foreach (var invalid in new[] { input with { Startup = new(OutpostStartupState.Ready) },
            input with { Startup = new(OutpostStartupState.Maintenance) }, input with { Startup = new(OutpostStartupState.RequiresInitialization) },
            input with { DomainFormat = OutpostDomainFormat.V1 }, input with { CurrentSchemaVerified = false }, input with { HasHistory = null },
            input with { Integrations = [] }, input with { Build = input.Build with { State = OutpostQualificationState.Pending } },
            input with { Build = input.Build with { AutomatedPrerequisitesVerified = false } } })
            Assert.False(OutpostReadiness.Evaluate(invalid).CanBindSuccessorWorld);
        var isolated = input with { Build = input.Build with { Mode = OutpostRuntimeMode.IsolatedAcceptance, State = OutpostQualificationState.Pending } };
        Assert.True(OutpostReadiness.Evaluate(isolated).CanBindSuccessorWorld);
        Assert.False(OutpostReadiness.Evaluate(isolated with { Build = isolated.Build with { IsolationVerified = false } }).CanBindSuccessorWorld);
    }
}
