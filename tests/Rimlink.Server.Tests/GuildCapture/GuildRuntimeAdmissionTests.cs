using Google.Protobuf;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildRuntimeAdmissionTests
{
    private static OutpostReadinessDecision Common()
    {
        var input=OutpostReadinessTests.Ready() with{DomainFormat=OutpostDomainFormat.V3,ConfiguredEnabled=false};
        return OutpostReadiness.Evaluate(input with{Build=input.Build with{Mode=OutpostRuntimeMode.IsolatedAcceptance,State=OutpostQualificationState.Pending}});
    }
    private static GuildVerifiedCut Cut()=>new("world","f9edbcf4-f778-45d0-8d67-67bc789e5ec9",Guid.NewGuid().ToString("D"),ByteString.CopyFrom(new byte[32]),new(1,"GuildCaptureIsolatedAcceptanceV1","f946e40b-c537-4b12-a933-d1e5039ce7a1",new string('b',64),new string('a',64),"world","f9edbcf4-f778-45d0-8d67-67bc789e5ec9"));
    private static GuildRuntimeGrant Grant(GuildVerifiedCut cut,GuildAcceptanceScope scope)=>new("f946e40b-c537-4b12-a933-d1e5039ce7a1",new string('b',64),new string('a',64),cut.WorldId,cut.Boundary,scope);
    [Theory]
    [InlineData((int)GuildRuntimeUse.Transport,true)][InlineData((int)GuildRuntimeUse.Setup,true)][InlineData((int)GuildRuntimeUse.Recovery,true)]
    [InlineData((int)GuildRuntimeUse.Simulation,false)][InlineData((int)GuildRuntimeUse.Economy,false)]
    public void HOME_admits_only_the_pre_Start_mirror_and_cleanup_slice(int rawUse,bool allowed)
    {
        var use=(GuildRuntimeUse)rawUse;var cut=Cut();var grant=Grant(cut,GuildAcceptanceScope.Home);
        if(allowed)GuildRuntimeAdmission.Require(Common(),cut,grant,use);
        else Assert.Throws<OutpostEvidenceUnavailableException>(()=>GuildRuntimeAdmission.Require(Common(),cut,grant,use));
    }
    [Theory]
    [InlineData("source")][InlineData("world")][InlineData("boundary")][InlineData("readiness")][InlineData("product")][InlineData("none")]
    public void An_acceptance_grant_never_bypasses_common_readiness_source_or_lineage(string fault)
    {
        var cut=Cut();var grant=Grant(cut,GuildAcceptanceScope.Slice);var common=Common();
        switch(fault){case "source":grant=grant with{SourceIdentity=new string('c',64)};break;case "world":grant=grant with{WorldId="other"};break;case "boundary":grant=grant with{Boundary=Guid.NewGuid().ToString("D")};break;
            case "readiness":common=common with{CanResolveCustody=false};break;case "product":common=common with{Mode=OutpostRuntimeMode.Product};break;case "none":grant=null!;break;}
        Assert.Throws<OutpostEvidenceUnavailableException>(()=>GuildRuntimeAdmission.Require(common,cut,grant,GuildRuntimeUse.Transport));
    }
    [Fact]
    public void SLICE_includes_simulation_and_money_but_default_product_has_no_implicit_grant()
    {
        var cut=Cut();var grant=Grant(cut,GuildAcceptanceScope.Slice);GuildRuntimeAdmission.Require(Common(),cut,grant,GuildRuntimeUse.Simulation);
        GuildRuntimeAdmission.Require(Common(),cut,grant,GuildRuntimeUse.Economy);Assert.Null(new GuildProductRuntimeAdmission().Observe());
    }
    [Fact]
    public void Product_build_contains_its_own_qualification_record()
    {
        using var stream = typeof(OutpostFeature).Assembly.GetManifestResourceStream("Rimlink.GuildCapture.Qualification");
        Assert.NotNull(stream);
    }

    [Theory]
    [InlineData("ready")][InlineData("off")][InlineData("pending")][InlineData("stale")]
    [InlineData("unqualified-common")][InlineData("isolated-proof")][InlineData("unverified-schema")][InlineData("not-v3")]
    [InlineData("maintenance")][InlineData("no-cut")][InlineData("test-cut")]
    public void Product_admission_requires_opt_in_two_matching_seals_and_a_verified_production_cut(string condition)
    {
        var input = OutpostReadinessTests.Ready();
        var build = input.Build with
        {
            State = condition == "unqualified-common" ? OutpostQualificationState.Pending : OutpostQualificationState.Qualified,
            IsolationVerified = condition == "isolated-proof",
        };
        var source = condition switch { "pending" => null, "stale" => new string('e', 64), _ => build.SourceIdentity };
        var feature = new OutpostFeature(Options.Create(new OutpostOptions()), new Build(build), guildAdmission: new ProductSeal(source),
            guildOptions: Options.Create(new GuildCaptureOptions { Enabled = condition != "off" }));
        feature.ObserveDomain(condition == "not-v3" ? OutpostDomainFormat.V2 : OutpostDomainFormat.V3, condition != "unverified-schema");
        feature.Startup = new(condition == "maintenance" ? OutpostStartupState.Maintenance : OutpostStartupState.Ready);
        feature.GuildCut = condition switch { "test-cut" => Cut(), "no-cut" => null, _ => Cut() with { Acceptance = null } };

        Assert.Equal(condition is not ("off" or "pending" or "stale" or "unqualified-common" or "isolated-proof"), feature.CanActivateGuildFormat);
        if (condition == "ready")
            feature.RequireGuildReadiness(GuildRuntimeUse.Economy);
        else
            Assert.Throws<OutpostEvidenceUnavailableException>(() => feature.RequireGuildReadiness(GuildRuntimeUse.Economy));
        Assert.False(feature.IsAvailable);
    }

    private sealed class Build(OutpostBuildQualification value) : IOutpostBuildQualification
    {
        public OutpostBuildQualification Observe() => value;
    }

    private sealed class ProductSeal(string? source) : IGuildRuntimeAdmission
    {
        public string? QualifiedProductSource => source;
        public GuildRuntimeGrant? Observe() => null;
    }
}
