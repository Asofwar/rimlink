using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleControlTransportTests
{
    private static BattleControlTransport Transport(GuildFixture f)=>new(f.Executors,new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Artifacts,f.Sessions,f.Time,new(),f.Clock);
    internal static async Task<(BattleControlAccess Access,BattleStartRequest Request,BattlePreparedManifestV1 Manifest)> Started(GuildFixture f,bool runtimeBound=true)
    {
        var(request,manifest)=await GuildBattleStartTests.Prepare(f,true);var start=await f.Service.StartBattleAsync(request,default);
        if(runtimeBound)await Transport(f).RuntimeStartedAsync(BattleRuntimeStartedTests.Request(request,manifest),default);
        byte[] secret=SHA256.HashData("transport secret"u8);
        var grant=await f.Service.ReconcileBattleControlAsync(f.Sessions.ByPlayer(2)!,new(){ClientRequestId=Guid.NewGuid().ToString("D"),Scope=start.Scope.Clone(),
            StartDigest=start.ManifestDigest,CapabilityDigest=ByteString.CopyFrom(SHA256.HashData(secret))},default);
        return(new(){Scope=start.Scope.Clone(),StartDigest=start.ManifestDigest,GrantId=grant.GrantId,Capability=ByteString.CopyFrom(secret),ClientChannelId=Guid.NewGuid().ToString("D")},request,manifest);
    }
    private static BattleAttachControlRequest Attach(BattleControlAccess access)=>new(){ClientRequestId=Guid.NewGuid().ToString("D"),Access=access.Clone()};
    private static BattleSubmitCommandRequest Command(BattleControlAccess access,BattlePreparedManifestV1 manifest)=>new(){ClientRequestId=Guid.NewGuid().ToString("D"),Access=access.Clone(),
        Command=new BattleCommandV1{Version=1,FighterId=manifest.Entities[0].FighterId,Move=new(){X=25,Z=25}}.ToByteString()};

    [Fact]
    public async Task Only_the_started_executor_can_publish_and_the_bound_commander_can_read_running_frames()
    {
        using var f=await GuildFixture.Create(true);
        var(access,request,manifest)=await Started(f,runtimeBound:false);
        var service=Transport(f);var session=f.Sessions.ByPlayer(2)!;var executor=BattleResultStaging.Lease(request.Executor);
        var first=new BattleRunningViewV1{Version=1,Scope=request.Scope.Clone(),StartDigest=access.StartDigest,
            Sequence=1,Tick=manifest.InitialTick};
        foreach(var fighter in manifest.Entities)first.Pawns.Add(new BattleRunningPawn{Spawned=true,Pawn=new()
        {
            FighterId=fighter.FighterId,EntityId=fighter.EntityId,Side=fighter.Side,X=25,Z=25,Alive=true,CanFight=true,Health=1
        }});
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(()=>service.PollAsync(executor,request.Scope,default,first.ToByteString()));
        await service.RuntimeStartedAsync(BattleRuntimeStartedTests.Request(request,manifest),default);
        access.ConnectionEpoch=(await service.AttachAsync(session,"connection-a",Attach(access),default)).ConnectionEpoch;
        var read=new BattleRunningViewRequest{Access=access.Clone()};
        Assert.True((await service.ReadViewAsync(session,"connection-a",read,default)).View.IsEmpty);
        string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;

        await service.PollAsync(executor,request.Scope,default,first.ToByteString());
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(()=>service.ReadViewAsync(session,"other-socket",read,default));
        Assert.Equal(first.ToByteString(),(await service.ReadViewAsync(session,"connection-a",read,default)).View);
        var next=first.Clone();next.Tick+=4;next.Sequence+=4;next.Pawns[0].Pawn.X++;
        await service.PollAsync(executor,request.Scope,default,next.ToByteString());
        read.AfterSequence=first.Sequence;
        var delta=BattleManifestContracts.ReadRunningView((await service.ReadViewAsync(session,"connection-a",read,default)).View.Span,manifest);
        Assert.Equal(first.Sequence,delta.BaseSequence);Assert.Equal(next.Pawns[0],Assert.Single(delta.Pawns));
        read.AfterSequence=0;
        Assert.Equal(next.ToByteString(),(await service.ReadViewAsync(session,"connection-a",read,default)).View);
        Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
    }

    [Fact]
    public async Task Only_the_bound_session_and_socket_can_append_and_retries_preserve_the_sequence()
    {
        using var f=await GuildFixture.Create(true);var(access,request,manifest)=await Started(f);var service=Transport(f);var session=f.Sessions.ByPlayer(2)!;
        var attach=Attach(access);var channel=await service.AttachAsync(session,"connection-a",attach,default);access.ConnectionEpoch=channel.ConnectionEpoch;
        Assert.Equal(channel,await service.AttachAsync(session,"connection-a",attach,default));
        await Assert.ThrowsAnyAsync<Exception>(()=>service.AttachAsync(session,"connection-b",attach,default));
        var command=Command(access,manifest);var first=await service.SubmitAsync(session,"connection-a",command,default);
        Assert.Equal(first,await service.SubmitAsync(session,"connection-a",command,default));Assert.Equal(1,first.Sequence);
        await Assert.ThrowsAnyAsync<Exception>(()=>service.SubmitAsync(session,"connection-b",command,default));
        var wrong=command.Clone();wrong.Access.Capability=ByteString.CopyFrom(new byte[32]);await Assert.ThrowsAnyAsync<Exception>(()=>service.SubmitAsync(session,"connection-a",wrong,default));
        var poll=await service.PollAsync(BattleResultStaging.Lease(request.Executor),request.Scope,default);
        Assert.Single(poll.Commands);Assert.Equal(first.Sequence,poll.Commands[0].Sequence);Assert.NotEmpty(poll.DeliveryId);
        Assert.Equal(poll,await service.PollAsync(BattleResultStaging.Lease(request.Executor),request.Scope,default));
        using var file=File.Open(Path.Combine(f.Artifacts.CommandDirectoryHeld(manifest.BattleId,manifest.Fence),manifest.BattleId+".commands"),FileMode.Open,FileAccess.Read,FileShare.None);
    }
    [Fact]
    public async Task Revocation_keeps_dispatched_execution_truth_and_replacement_waits_for_CutApplied()
    {
        using var f=await GuildFixture.Create(true);var(access,request,manifest)=await Started(f);var service=Transport(f);var session=f.Sessions.ByPlayer(2)!;
        access.ConnectionEpoch=(await service.AttachAsync(session,"connection-a",Attach(access),default)).ConnectionEpoch;
        await service.SubmitAsync(session,"connection-a",Command(access,manifest),default);
        var executor=BattleResultStaging.Lease(request.Executor);var sent=await service.PollAsync(executor,request.Scope,default);
        f.Sessions.End(session.Token);
        var cut=await service.PollAsync(executor,request.Scope,default);Assert.Single(cut.Cuts);Assert.Single(cut.Commands);Assert.False(cut.Controls.Single(c=>c.Side==BattleFighterSide.Attacker).Present);
        var ack=new BattleExecutorCommandsAck{Executor=request.Executor.Clone(),Scope=request.Scope.Clone(),DeliveryId=sent.DeliveryId};ack.AppliedCutIds.Add(cut.Cuts[0].CutId);
        await Assert.ThrowsAnyAsync<Exception>(()=>service.AcknowledgeAsync(ack,default));
        ack.Receipts.Add(new BattleCommandReceipt{Side=BattleFighterSide.Attacker,Sequence=1,CommandDigest=sent.Commands[0].CommandDigest,
            AcceptedTick=12,Disposition=BattleCommandDisposition.Executed});
        await service.AcknowledgeAsync(ack,default);await service.AcknowledgeAsync(ack,default);
        var journal=BattleCommandJournal.OpenHeld(f.Artifacts.CommandDirectoryHeld(manifest.BattleId,manifest.Fence),new(manifest.BattleId,manifest.WorldInstanceId,manifest.RestoreBoundary,manifest.ExecutorId,manifest.Fence,access.StartDigest.ToByteArray()),false);
        Assert.Equal(12,journal.Read(1,1).AcceptedTick);Assert.False(journal.CutPending(1));
    }
    [Fact]
    public async Task A_reconnected_commander_gets_one_new_channel_only_after_the_old_dispatch_and_cut_are_resolved()
    {
        using var f=await GuildFixture.Create(true);var(access,request,manifest)=await Started(f);var service=Transport(f);var old=f.Sessions.ByPlayer(2)!;
        access.ConnectionEpoch=(await service.AttachAsync(old,"old",Attach(access),default)).ConnectionEpoch;
        await service.SubmitAsync(old,"old",Command(access,manifest),default);var executor=BattleResultStaging.Lease(request.Executor);
        var sent=await service.PollAsync(executor,request.Scope,default);
        var current=f.Sessions.Start(await f.H.Db.Accounts.SingleAsync(a=>a.Id==2),await f.H.Db.Players.SingleAsync(v=>v.Id==2),"127.0.0.1",out _);current.GuildVersion=1;
        f.Time.Advance(TimeSpan.FromSeconds(5));byte[] secret=SHA256.HashData("replacement capability"u8);
        var grant=await f.Service.ReconcileBattleControlAsync(current,new(){ClientRequestId=Guid.NewGuid().ToString("D"),Scope=request.Scope.Clone(),StartDigest=access.StartDigest,
            CapabilityDigest=ByteString.CopyFrom(SHA256.HashData(secret)),ExpectedEpoch=2},default);
        var next=access.Clone();next.GrantId=grant.GrantId;next.Capability=ByteString.CopyFrom(secret);next.ConnectionEpoch=0;next.ClientChannelId=Guid.NewGuid().ToString("D");
        var attach=Attach(next);attach.ExpectedPriorEpoch=1;
        await Assert.ThrowsAnyAsync<Exception>(()=>service.AttachAsync(current,"new",attach,default));
        var closing=await service.PollAsync(executor,request.Scope,default);
        var ack=new BattleExecutorCommandsAck{Executor=request.Executor.Clone(),Scope=request.Scope.Clone(),DeliveryId=sent.DeliveryId};
        ack.Receipts.Add(new BattleCommandReceipt{Side=BattleFighterSide.Attacker,Sequence=1,CommandDigest=sent.Commands[0].CommandDigest,
            Disposition=BattleCommandDisposition.NotExecuted,Reason=BattleCommandReason.ControlRevoked});ack.AppliedCutIds.Add(Assert.Single(closing.Cuts).CutId);
        await service.AcknowledgeAsync(ack,default);
        next.ConnectionEpoch=(await service.AttachAsync(current,"new",attach,default)).ConnectionEpoch;Assert.Equal(2,next.ConnectionEpoch);
        Assert.Equal(2,(await service.SubmitAsync(current,"new",Command(next,manifest),default)).Sequence);
        await Assert.ThrowsAnyAsync<Exception>(()=>service.SubmitAsync(old,"old",Command(access,manifest),default));
    }
    [Fact]
    public async Task A_cold_transport_cache_closes_presence_without_claiming_the_old_dispatch_was_not_executed()
    {
        using var f=await GuildFixture.Create(true);var(access,request,manifest)=await Started(f);var service=Transport(f);var session=f.Sessions.ByPlayer(2)!;
        access.ConnectionEpoch=(await service.AttachAsync(session,"old",Attach(access),default)).ConnectionEpoch;
        await service.SubmitAsync(session,"old",Command(access,manifest),default);var executor=BattleResultStaging.Lease(request.Executor);
        var sent=await service.PollAsync(executor,request.Scope,default);var cold=Transport(f);var work=await cold.PollAsync(executor,request.Scope,default);
        Assert.Equal(sent.DeliveryId,work.DeliveryId);Assert.Single(work.Commands);Assert.Single(work.Cuts);
        Assert.False(work.Controls.Single(c=>c.Side==BattleFighterSide.Attacker).Present);
    }
    [Fact]
    public async Task A_full_command_queue_stays_bounded_and_the_hot_path_does_not_acquire_A_or_write_financial_rows()
    {
        using var f=await GuildFixture.Create(true);var(access,request,manifest)=await Started(f);var service=Transport(f);var session=f.Sessions.ByPlayer(2)!;
        access.ConnectionEpoch=(await service.AttachAsync(session,"connection",Attach(access),default)).ConnectionEpoch;
        string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        using(await f.H.Anchor.AcquireAsync(default))
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for(int i=0;i<64;i++){if(i>0 && i%20==0)f.Time.Advance(TimeSpan.FromSeconds(1));await service.SubmitAsync(session,"connection",Command(access,manifest),deadline.Token);}
            await Assert.ThrowsAnyAsync<Exception>(()=>service.SubmitAsync(session,"connection",Command(access,manifest),deadline.Token));
            Assert.Equal(8,(await service.PollAsync(BattleResultStaging.Lease(request.Executor),request.Scope,deadline.Token)).Commands.Count);
        }
        Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
    }
    [Fact]
    public async Task Peaceful_after_Start_does_not_take_defensive_control_away_from_the_nominated_defender()
    {
        using var f=await GuildFixture.Create(true);var(request,manifest)=await GuildBattleStartTests.Prepare(f,true);
        await f.Service.AssignDefenderCommanderAsync(1,f.Scope,Guid.NewGuid().ToString("D"),manifest.CampaignId,1,default);
        var start=await f.Service.StartBattleAsync(request,default);
        var player=await f.H.Db.Players.SingleAsync(p=>p.Id==1);player.Peaceful=true;await f.H.Db.SaveChangesAsync();f.H.Db.ChangeTracker.Clear();
        var session=f.Sessions.Start(await f.H.Db.Accounts.SingleAsync(a=>a.Id==1),await f.H.Db.Players.SingleAsync(p=>p.Id==1),"127.0.0.1",out _);session.GuildVersion=1;
        byte[] secret=SHA256.HashData("defender capability"u8);
        var grant=await f.Service.ReconcileBattleControlAsync(session,new(){ClientRequestId=Guid.NewGuid().ToString("D"),Scope=request.Scope.Clone(),StartDigest=start.ManifestDigest,
            CapabilityDigest=ByteString.CopyFrom(SHA256.HashData(secret))},default);
        var access=new BattleControlAccess{Scope=request.Scope.Clone(),StartDigest=start.ManifestDigest,GrantId=grant.GrantId,Capability=ByteString.CopyFrom(secret),ClientChannelId=Guid.NewGuid().ToString("D")};
        Assert.Equal(BattleFighterSide.Defender,(await Transport(f).AttachAsync(session,"defender",Attach(access),default)).Side);
    }
    [Fact]
    public async Task New_commands_have_a_sliding_budget_but_retries_do_not_consume_it()
    {
        using var f=await GuildFixture.Create(true);var(access,request,manifest)=await Started(f);var service=Transport(f);var session=f.Sessions.ByPlayer(2)!;
        access.ConnectionEpoch=(await service.AttachAsync(session,"connection-a",Attach(access),default)).ConnectionEpoch;
        var retry=Command(access,manifest);await service.SubmitAsync(session,"connection-a",retry,default);
        for(int i=0;i<30;i++)await service.SubmitAsync(session,"connection-a",retry,default);
        for(int i=1;i<20;i++)await service.SubmitAsync(session,"connection-a",Command(access,manifest),default);
        await Assert.ThrowsAnyAsync<Exception>(()=>service.SubmitAsync(session,"connection-a",Command(access,manifest),default));
        f.Time.Advance(TimeSpan.FromSeconds(1));await service.SubmitAsync(session,"connection-a",Command(access,manifest),default);
        var records=await f.H.Db.Set<GuildCaptureOperation>().AsNoTracking().CountAsync();
        await service.HeartbeatAsync(session,"connection-a",access,default);Assert.Equal(records,await f.H.Db.Set<GuildCaptureOperation>().CountAsync());
    }
}
