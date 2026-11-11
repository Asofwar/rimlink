using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Xunit;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildRuntimeNegotiationTests
{
    private sealed class Common(bool qualified):IOutpostBuildQualification
    {public OutpostBuildQualification Observe()=>new(OutpostRuntimeMode.IsolatedAcceptance,OutpostQualificationState.Pending,new string('a',64),qualified,qualified);}
    private sealed class Admission(string stand):IGuildRuntimeAdmission
    {public string? StandId=>stand;public GuildRuntimeGrant? Observe()=>null;}
    [Fact]
    public async Task Real_registration_refuses_implicit_selection_before_creating_an_account_then_echoes_the_explicit_stand()
    {
        string stand=Guid.NewGuid().ToString("D");
        using var fixture=new ServerFixture().WithoutWorld().WithSetting("Auth:AllowPassword","true").WithSetting("Auth:AdminLogins:0","attacker")
            .WithServices(services=>{services.AddSingleton<IOutpostBuildQualification>(new Common(true));services.AddSingleton<IGuildRuntimeAdmission>(new Admission(stand));});
        var api=new Rimlink.Protocol.V1.Auth.AuthClient(fixture.CreateChannel());var request=new RegisterRequest{Login="attacker",Password="unit-password",PlayerName="Attacker",GuildVersion=1};
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(()=>api.RegisterAsync(request).ResponseAsync);
        using(var scope=fixture.Services.CreateScope())Assert.False(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Accounts.AnyAsync());
        request.GuildRuntime=new(){Mode=GuildRuntimeMode.IsolatedAcceptanceV1,StandId=stand};
        var reply=await api.RegisterAsync(request);Assert.NotEmpty(reply.SessionToken);Assert.Equal(request.GuildRuntime,reply.GuildRuntime);
        Assert.Equal(0u,reply.GuildVersion);
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(()=>api.LoginAsync(new(){Password=new(){Login="attacker",Password="unit-password"}}).ResponseAsync);
    }
    [Theory]
    [InlineData("valid")][InlineData("default-client")][InlineData("other-stand")][InlineData("mode")][InlineData("unqualified")][InlineData("product")]
    public void Local_acceptance_requires_explicit_exact_stand_before_any_login_effect(string fault)
    {
        string stand=Guid.NewGuid().ToString("D");var feature=new OutpostFeature(Options.Create(new OutpostOptions()),new Common(fault!="unqualified"),guildAdmission:fault=="product"?null:new Admission(stand));
        GuildRuntimeTarget? target=fault=="default-client"?null:new(){Mode=fault=="mode"?GuildRuntimeMode.Unspecified:GuildRuntimeMode.IsolatedAcceptanceV1,StandId=fault=="other-stand"?Guid.NewGuid().ToString("D"):stand};
        if(fault=="valid")Assert.Equal(target,GuildRuntimeNegotiation.Require(feature,target));
        else Assert.Throws<global::Grpc.Core.RpcException>(()=>GuildRuntimeNegotiation.Require(feature,target));
        if(fault=="product")Assert.Null(GuildRuntimeNegotiation.Require(feature,null));
    }
}
