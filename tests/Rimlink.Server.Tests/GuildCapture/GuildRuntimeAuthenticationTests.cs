using Microsoft.Extensions.Options;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildRuntimeAuthenticationTests
{
    private sealed class Common:IOutpostBuildQualification
    {public OutpostBuildQualification Observe()=>new(OutpostRuntimeMode.IsolatedAcceptance,OutpostQualificationState.Pending,new string('a',64),true,true);}
    private sealed class Admission(GuildRuntimeGrant grant):IGuildRuntimeAdmission
    {public GuildRuntimeGrant? Observe()=>grant;}
    [Theory]
    [InlineData("valid")][InlineData("missing-cut")][InlineData("unqualified")][InlineData("token")]
    public void Product_flag_OFF_needs_the_registered_scoped_isolated_provider_and_complete_dedicated_configuration(string fault)
    {
        string boundary=Guid.NewGuid().ToString("D"),stand=Guid.NewGuid().ToString("D");var grant=new GuildRuntimeGrant(stand,new string('b',64),new string('a',64),"world",boundary,GuildAcceptanceScope.Home);
        var feature=new OutpostFeature(Options.Create(new OutpostOptions()),new Common(),guildAdmission:new Admission(grant));
        feature.ObserveDomain(OutpostDomainFormat.V3,true);feature.HasHistoryObservation=true;feature.Startup=new(OutpostStartupState.Ready);
        feature.GuildCut=new("world",boundary,Guid.NewGuid().ToString("D"),Google.Protobuf.ByteString.CopyFrom(new byte[32]),new(1,"GuildCaptureIsolatedAcceptanceV1",stand,grant.BootstrapDigest,grant.SourceIdentity,"world",boundary));
        var options=new GuildCaptureOptions{ExecutorId="owned-executor",ExecutorTokenSha256=new string('c',64),ApprovedProfileDigest=new string('d',64)};
        if(fault=="missing-cut")feature.GuildCut=null;if(fault=="unqualified")feature.Startup=new(OutpostStartupState.Maintenance);if(fault=="token")options.ExecutorTokenSha256="invalid";
        if(fault=="valid")BattleExecutorAuthentication.RequireTransport(options,feature);
        else Assert.Throws<global::Grpc.Core.RpcException>(()=>BattleExecutorAuthentication.RequireTransport(options,feature));
        Assert.False(options.Enabled);Assert.Throws<global::Grpc.Core.RpcException>(()=>BattleExecutorAuthentication.RequireTransport(options,null));
    }
}
