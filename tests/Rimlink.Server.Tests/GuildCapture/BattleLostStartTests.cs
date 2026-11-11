using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleLostStartTests
{
    private static BattleExecutorHelloService Service(GuildFixture f)=>new(f.Executors,new(f.Time),new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Profiles,f.Artifacts,f.Clock);
    private static BattleExecutorHelloBindingV1 Pending(BattleExecutorHelloBindingV1 binding,BattleStartRequest request,BattlePreparedManifestV1 start)
    {
        binding=binding.Clone();binding.Entry=null;binding.BattleId=start.BattleId;binding.Attempt=start.Attempt;binding.Fence=start.Fence;binding.AssignmentTransitionId=start.PreparationTransitionId;binding.StartDigest=request.ManifestDigest;
        binding.RetainedContext=new(){Version=1,Phase=BattleExecutorRetainedPhase.StartAcknowledgementPending,StartNonce=start.StartNonce,GameLoaded=true,CurrentTick=start.InitialTick,
            StartRequestId=request.ClientRequestId,InitialSnapshot=start.InitialSnapshot.Clone()};return binding;
    }
    [Fact]
    public async Task A_lost_Start_reply_after_server_restart_only_replays_the_original_request_before_one_durable_runtime_binding()
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,true);var original=await f.Service.StartBattleAsync(request,default);
        var binding=Pending(f.Executors.BindingOf(BattleResultStaging.Lease(request.Executor))!,request,start);f.UseExecutorProfile(f.ExpectedProfile);
        var service=Service(f);var challenge=await service.BeginAsync(binding,"credential",default);
        var lease=await service.CompleteAsync(new(){Binding=binding,ChallengeId=challenge.ChallengeId,Nonce=challenge.Nonce,ServerBootId=challenge.ServerBootId},"credential",default);
        Assert.True(lease.StartReplayOnly);var executor=BattleResultStaging.Lease(lease);Assert.Throws<InvalidDataException>(()=>f.Executors.RequireExecution(executor));
        var retry=request.Clone();retry.Executor=lease.Clone();Assert.Equal(original,await f.Service.StartBattleAsync(retry,default));
        retry.ClientRequestId=Guid.NewGuid().ToString("D");await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.StartBattleAsync(retry,default));
        string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        var proof=BattleRuntimeStartedTests.Request(request,start);proof.Executor=lease.Clone();var ready=await BattleRuntimeStartedTests.Transport(f).RuntimeStartedAsync(proof,default);
        Assert.False(ready.Executor.StartReplayOnly);f.Executors.RequireExecution(executor);Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        await Assert.ThrowsAnyAsync<Exception>(()=>service.BeginAsync(binding,"credential",default));
    }
    [Theory]
    [InlineData("tick")][InlineData("instance")][InlineData("initial")][InlineData("request")][InlineData("controller")][InlineData("deadline")]
    public async Task Start_pending_cannot_guess_a_new_runtime_initial_or_authorization(string fault)
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,true);await f.Service.StartBattleAsync(request,default);
        var binding=Pending(f.Executors.BindingOf(BattleResultStaging.Lease(request.Executor))!,request,start);
        switch(fault){case "tick":binding.RetainedContext.CurrentTick++;break;case "instance":binding.RuntimeInstanceId=Guid.NewGuid().ToString("D");break;
            case "initial":binding.RetainedContext.InitialSnapshot.SizeBytes++;break;case "request":binding.RetainedContext.StartRequestId=Guid.NewGuid().ToString("D");break;
            case "controller":binding.RetainedContext.TickRuntimeBound=true;break;case "deadline":f.Time.Advance(TimeSpan.FromMinutes(20));break;}
        await Assert.ThrowsAnyAsync<Exception>(()=>Service(f).BeginAsync(binding,"credential",default));
    }
}
