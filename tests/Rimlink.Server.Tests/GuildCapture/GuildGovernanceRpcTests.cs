using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Tests.Support;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildGovernanceRpcTests
{
    [Fact]
    public async Task Ordinary_OFF_host_refuses_all_setup_and_money_RPCs_even_when_the_caller_has_the_new_client_version()
    {
        using var f=new ServerFixture().WithSetting("Auth:AllowPassword","true");var login=await f.RegisterAndLogin("guild-api-test");
        var session=f.Services.GetRequiredService<SessionRegistry>().ByPlayer(login.Player.PlayerId)!;session.GuildVersion=1;
        var api=new Rimlink.Protocol.V1.GuildCapture.GuildCaptureClient(f.CreateChannel());var headers=GrpcTestExtensions.Bearer(login.SessionToken);
        Func<Task>[] calls=[()=>api.ListOutpostsAsync(new(),headers).ResponseAsync,()=>api.PreviewConsentAsync(new(),headers).ResponseAsync,
            ()=>api.AcceptConsentAsync(new(),headers).ResponseAsync,()=>api.DeclareCampaignAsync(new(),headers).ResponseAsync,()=>api.AssignDefenderCommanderAsync(new(),headers).ResponseAsync,
            ()=>api.ClaimResourcesAsync(new(),headers).ResponseAsync];
        foreach(var call in calls)
        {
            var error=await Assert.ThrowsAsync<RpcException>(call);Assert.Equal(StatusCode.FailedPrecondition,error.StatusCode);Assert.Equal(ErrorCode.GuildUnavailable,error.ErrorDetail()!.Code);
        }
    }
    [Fact]
    public async Task Defender_assignment_RPC_requires_the_current_defending_leader_and_replays_one_exact_receipt()
    {
        using var f=await GuildFixture.Create(true);var(_,_,manifest)=await BattlePreparedAdmissionTests.Prepare(f);
        using var server=new ServerFixture().WithSetting("Auth:AllowPassword","true").WithServices(services=>
            services.AddScoped<GuildCaptureGrpcService>(_=>new(f.Service,f.H.Authority,null!,null!,null!)));
        var defender=await server.RegisterAndLogin("defender-assign");var attacker=await server.RegisterAndLogin("attacker-assign");
        Assert.Equal(1,defender.Player.PlayerId);Assert.Equal(2,attacker.Player.PlayerId);
        var sessions=server.Services.GetRequiredService<SessionRegistry>();
        var api=new Protocol.V1.GuildCapture.GuildCaptureClient(server.CreateChannel());var headers=GrpcTestExtensions.Bearer(defender.SessionToken);
        var request=new GuildDefenderCommanderAssignment{Scope=new(){WorldInstanceId=f.Scope.WorldId,RestoreBoundary=f.Scope.Boundary},
            CampaignId=manifest.CampaignId,CommanderId=1,ClientRequestId=Guid.NewGuid().ToString("D")};
        var old=await Assert.ThrowsAsync<RpcException>(()=>api.AssignDefenderCommanderAsync(request,headers).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition,old.StatusCode);
        Assert.Equal(ErrorCode.RecoveryClientRequired,old.ErrorDetail()!.Code);
        sessions.ByPlayer(1)!.GuildVersion=1;sessions.ByPlayer(2)!.GuildVersion=1;
        var enemy=await Assert.ThrowsAsync<RpcException>(()=>api.AssignDefenderCommanderAsync(request,GrpcTestExtensions.Bearer(attacker.SessionToken)).ResponseAsync);
        Assert.Equal(ErrorCode.GuildUnavailable,enemy.ErrorDetail()!.Code);
        Assert.Null((await f.H.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync()).DefenderCommanderId);
        var first=await api.AssignDefenderCommanderAsync(request,headers);Assert.Equal(request.CampaignId,first.CampaignId);Assert.Equal(1,first.CommanderId);
        Assert.Equal(first,await api.AssignDefenderCommanderAsync(request,headers));
        request.CommanderId=2;await Assert.ThrowsAsync<RpcException>(()=>api.AssignDefenderCommanderAsync(request,headers).ResponseAsync);
        Assert.Equal(1,(await f.H.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync()).DefenderCommanderId);
        Assert.Equal(first.TransitionId,(await f.H.Db.Set<GuildCaptureOperation>().AsNoTracking().SingleAsync(o=>o.Kind=="GuildDefenderCommanderAssigned")).TransitionId);
    }
}
