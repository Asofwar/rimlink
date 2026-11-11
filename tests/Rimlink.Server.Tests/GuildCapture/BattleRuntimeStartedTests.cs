using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleRuntimeStartedTests
{
    internal static BattleControlTransport Transport(GuildFixture f)=>new(f.Executors,new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Artifacts,f.Sessions,f.Time,new(),f.Clock);
    internal static BattleRuntimeStartedRequest Request(BattleStartRequest request,BattlePreparedManifestV1 start)=>new(){Executor=request.Executor.Clone(),Proof=new(){Version=1,
        Scope=request.Scope.Clone(),RuntimeInstanceId=start.RuntimeInstanceId,AssignmentTransitionId=start.PreparationTransitionId,StartDigest=request.ManifestDigest,
        StartRequestId=request.ClientRequestId,InitialSnapshot=start.InitialSnapshot.Clone(),InitialTick=start.InitialTick,GameLoaded=true,TickRuntimeBound=true,
        ReceiptJournalSha256=ByteString.CopyFrom(SHA256.HashData("initial native receipt journal fixture"u8)),ReceiptJournalBytes=40}};
    [Fact]
    public async Task Runtime_binding_is_durable_idempotent_transport_evidence_and_never_another_Start_or_financial_write()
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,true);await f.Service.StartBattleAsync(request,default);
        var service=Transport(f);string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;var proof=Request(request,start);
        var ready=await service.RuntimeStartedAsync(proof,default);var again=await service.RuntimeStartedAsync(proof,default);
        Assert.Equal(ready,again);Assert.False(ready.Executor.StartReplayOnly);Assert.Equal(SHA256.HashData(proof.Proof.ToByteArray()),ready.ProofDigest.ToByteArray());
        var replay=await Transport(f).RuntimeStartedAsync(proof,default);Assert.Equal(ready,replay);Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        proof.Proof.ReceiptJournalSha256=ByteString.CopyFrom(new byte[32]);await Assert.ThrowsAnyAsync<Exception>(()=>service.RuntimeStartedAsync(proof,default));
    }
    [Theory]
    [InlineData("tick")][InlineData("runtime")][InlineData("initial")][InlineData("request")][InlineData("controller")]
    public async Task A_mismatched_or_unbound_controller_cannot_open_the_runtime_barrier(string fault)
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,true);await f.Service.StartBattleAsync(request,default);var proof=Request(request,start);
        switch(fault){case "tick":proof.Proof.InitialTick++;break;case "runtime":proof.Proof.RuntimeInstanceId=Guid.NewGuid().ToString("D");break;
            case "initial":proof.Proof.InitialSnapshot.SizeBytes++;break;case "request":proof.Proof.StartRequestId=Guid.NewGuid().ToString("D");break;case "controller":proof.Proof.TickRuntimeBound=false;break;}
        await Assert.ThrowsAnyAsync<Exception>(()=>Transport(f).RuntimeStartedAsync(proof,default));
    }
    [Fact]
    public async Task A_player_command_can_wait_but_cannot_be_dispatched_before_the_native_runtime_binding_is_durable()
    {
        using var f=await GuildFixture.Create(true);var(access,request,start)=await BattleControlTransportTests.Started(f,false);var service=Transport(f);var player=f.Sessions.ByPlayer(2)!;
        var attached=await service.AttachAsync(player,"socket",new(){ClientRequestId=Guid.NewGuid().ToString("D"),Access=access.Clone()},default);access.ConnectionEpoch=attached.ConnectionEpoch;
        await service.SubmitAsync(player,"socket",new(){ClientRequestId=Guid.NewGuid().ToString("D"),Access=access.Clone(),Command=new BattleCommandV1{Version=1,FighterId=start.Entities[0].FighterId,Move=new(){X=25,Z=25}}.ToByteString()},default);
        Assert.Empty((await service.PollAsync(BattleResultStaging.Lease(request.Executor),request.Scope,default)).Commands);
        await service.RuntimeStartedAsync(Request(request,start),default);
        Assert.Single((await service.PollAsync(BattleResultStaging.Lease(request.Executor),request.Scope,default)).Commands);
    }
}
