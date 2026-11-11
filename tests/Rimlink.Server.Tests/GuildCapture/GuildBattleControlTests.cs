using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildBattleControlTests
{
    private static BattleReconcileControlRequest Request(BattleStartAuthorization start)=>new(){ClientRequestId=Guid.NewGuid().ToString("D"),Scope=start.Scope.Clone(),
        StartDigest=start.ManifestDigest,CapabilityDigest=ByteString.CopyFrom(SHA256.HashData("fixture secret"u8))};
    private static async Task<Session> Login(GuildFixture f,long player)
    {
        var session=f.Sessions.Start(await f.H.Db.Accounts.SingleAsync(a=>a.Id==player),await f.H.Db.Players.SingleAsync(p=>p.Id==player),"127.0.0.1",out _);session.GuildVersion=1;return session;
    }
    [Fact]
    public async Task Relogin_replaces_only_control_with_a_new_epoch_and_exact_replay_preserves_it()
    {
        using var f=await GuildFixture.Create(true);var(request,manifest)=await GuildBattleStartTests.Prepare(f,true);
        var start=await f.Service.StartBattleAsync(request,default);var before=(await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).StartManifest!;
        var session=await Login(f,2);var control=Request(start);control.ExpectedEpoch=1;
        var first=await f.Service.ReconcileBattleControlAsync(session,control,default);
        Assert.Equal(first,await f.Service.ReconcileBattleControlAsync(session,control,default));Assert.Equal(2,first.Epoch);Assert.Equal(BattleFighterSide.Attacker,first.Side);
        var grants=await f.H.Db.Set<GuildBattleControlGrant>().AsNoTracking().ToArrayAsync();Assert.Single(grants,g=>g.Active);
        Assert.False(grants.Single(g=>g.Epoch==1).Active);Assert.Equal(before,(await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).StartManifest);
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        var newer=await Login(f,2);await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.ReconcileBattleControlAsync(session,control,default));
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.ReconcileBattleControlAsync(newer,control,default));
        f.Time.Advance(TimeSpan.FromSeconds(5));var next=Request(start);next.ExpectedEpoch=2;
        Assert.Equal(3,(await f.Service.ReconcileBattleControlAsync(newer,next,default)).Epoch);
    }
    [Fact]
    public async Task Nominated_defender_can_join_an_AI_baseline_without_DefenderReady_or_a_new_Start()
    {
        using var f=await GuildFixture.Create(true);var(request,manifest)=await GuildBattleStartTests.Prepare(f,true);
        var defenderCampaign = Assert.Single((await f.Service.ListOutpostsAsync(1, f.Scope, default)).Campaigns);
        Assert.Equal(manifest.CampaignId, defenderCampaign.CampaignId);
        Assert.True(defenderCampaign.Defending);
        Assert.True(defenderCampaign.CanAssignDefender);
        Assert.Equal(0, defenderCampaign.CommanderId);
        var attackerCampaign = Assert.Single((await f.Service.ListOutpostsAsync(2, f.Scope, default)).Campaigns);
        Assert.Equal(manifest.CampaignId, attackerCampaign.CampaignId);
        Assert.False(attackerCampaign.Defending);
        Assert.False(attackerCampaign.CanAssignDefender);
        Assert.Equal(2, attackerCampaign.CommanderId);
        Assert.Empty((await f.Service.ListOutpostsAsync(3, f.Scope, default)).Campaigns);
        var assigned=await f.Service.AssignDefenderCommanderAsync(1,f.Scope,Guid.NewGuid().ToString("D"),manifest.CampaignId,1,default);
        await f.Service.AssignDefenderCommanderAsync(1,f.Scope,Guid.NewGuid().ToString("D"),manifest.CampaignId,1,default);
        var start=await f.Service.StartBattleAsync(request,default);var session=await Login(f,1);
        var runningCampaign = Assert.Single((await f.Service.ListOutpostsAsync(1, f.Scope, default)).Campaigns);
        Assert.Equal(1, runningCampaign.CommanderId);
        Assert.False(runningCampaign.CanAssignDefender);
        var first=await f.Service.ReconcileBattleControlAsync(session,Request(start),default);
        Assert.Equal(BattleFighterSide.Defender,first.Side);Assert.Equal(1,first.Epoch);Assert.Equal(start.ManifestDigest,first.StartDigest);
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.AssignDefenderCommanderAsync(1,f.Scope,Guid.NewGuid().ToString("D"),manifest.CampaignId,1,default));
    }
    [Fact]
    public async Task Loss_of_the_defender_session_does_not_invalidate_the_attackers_exact_ready_Start()
    {
        using var f=await GuildFixture.Create(true);var(service,executor,manifest)=await BattlePreparedAdmissionTests.Prepare(f);
        await f.Service.AssignDefenderCommanderAsync(1,f.Scope,Guid.NewGuid().ToString("D"),manifest.CampaignId,1,default);
        var defender=await Login(f,1);var member=await f.H.Db.Set<AllianceMember>().AsNoTracking().SingleAsync(m=>m.PlayerId==1);
        manifest.Defender=new(){PlayerId=1,AllianceId=50,ControlGrantId=Guid.NewGuid().ToString("D"),ControlEpoch=1,
            SessionDigest=BattleCommanderIdentity.Digest(defender),MembershipJoinedUtcTicks=member.JoinedAt.Ticks};
        var proposed=await service.ProposeAsync(BattlePreparedAdmissionTests.Request(executor,manifest),default);
        await f.Service.AttackerReadyAsync(f.Sessions.ByPlayer(2)!,GuildBattleReadyTests.Request(manifest,proposed),default);
        f.Sessions.End(defender.Token);
        var start=await f.Service.StartBattleAsync(new(){ClientRequestId=Guid.NewGuid().ToString("D"),Executor=new(){ExecutorId=executor.ExecutorId,LeaseId=executor.LeaseId,
            Fence=executor.Fence,ProfileDigest=executor.ProfileDigest},Scope=proposed.Artifact.Scope.Clone(),ManifestDigest=proposed.ManifestDigest},default);
        Assert.Equal(manifest.ToByteArray(),start.Manifest.ToByteArray());
        Assert.Single(await f.H.Db.Set<GuildBattleControlGrant>().ToArrayAsync());
        var reconnected=await Login(f,1);var grant=await f.Service.ReconcileBattleControlAsync(reconnected,Request(start),default);
        Assert.Equal(BattleFighterSide.Defender,grant.Side);Assert.Equal(2,grant.Epoch);
    }

    [Theory]
    [InlineData("not-started")][InlineData("wrong-digest")][InlineData("wrong-boundary")][InlineData("wrong-epoch")]
    [InlineData("not-nominated")][InlineData("frozen")][InlineData("membership")][InlineData("revoked")]
    public async Task Invalid_control_never_mints_a_capability_or_changes_the_match(string damage)
    {
        using var f=await GuildFixture.Create(true);var(request,manifest)=await GuildBattleStartTests.Prepare(f,true);
        var start=damage=="not-started"?new BattleStartAuthorization{Scope=request.Scope.Clone(),ManifestDigest=request.ManifestDigest}:await f.Service.StartBattleAsync(request,default);
        var session=f.Sessions.ByPlayer(2)!;var control=Request(start);
        switch(damage){
            case "wrong-digest":control.StartDigest=ByteString.CopyFrom(new byte[32]);break;
            case "wrong-boundary":control.Scope.RestoreBoundary=Guid.NewGuid().ToString("D");break;
            case "wrong-epoch":control.ExpectedEpoch=100;break;
            case "not-nominated":session=await Login(f,1);break;
            case "frozen":var player=await f.H.Db.Players.SingleAsync(p=>p.Id==2);player.EconomyFrozenAt=f.Time.Now.UtcDateTime;await f.H.Db.SaveChangesAsync();f.H.Db.ChangeTracker.Clear();break;
            case "membership":await f.H.Db.Set<AllianceMember>().Where(m=>m.PlayerId==2).ExecuteDeleteAsync();break;
            case "revoked":await f.Service.RevokeBattleProfileAsync(Actor.Api,f.Scope,Guid.NewGuid().ToString("D"),manifest.ApprovalTransitionId,"fixture revoke",default);break;
        }
        Assert.NotNull(await Record.ExceptionAsync(()=>f.Service.ReconcileBattleControlAsync(session,control,default)));
        Assert.Empty(await f.H.Db.Set<GuildCaptureOperation>().Where(o=>o.Kind=="GuildBattleControlReconciled").ToArrayAsync());
        Assert.Single(await f.H.Db.Set<GuildBattleControlGrant>().ToArrayAsync());
    }
    [Theory]
    [InlineData("BeforePending",false)][InlineData("AfterPending",false)][InlineData("AfterDomainCommit",true)][InlineData("AfterExternalCommit",true)]
    public async Task Lost_control_reply_replays_the_original_grant_after_authority_recovery(string point,bool committed)
    {
        using var f=await GuildFixture.Create(true);var(request,_)=await GuildBattleStartTests.Prepare(f,true);var start=await f.Service.StartBattleAsync(request,default);
        var session=f.Sessions.ByPlayer(2)!;var control=Request(start);
        f.H.Authority.CommitCheckpoint=p=>{if(p==point)throw new Interrupted();};
        await Assert.ThrowsAsync<Interrupted>(()=>f.Service.ReconcileBattleControlAsync(session,control,default));f.H.Authority.CommitCheckpoint=null;f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed,await f.H.Db.Set<GuildCaptureOperation>().AnyAsync(o=>o.Kind=="GuildBattleControlReconciled"));
        var reply=await f.Service.ReconcileBattleControlAsync(session,control,default);Assert.Equal(reply,await f.Service.ReconcileBattleControlAsync(session,control,default));
        Assert.Single(await f.H.Db.Set<GuildBattleControlGrant>().Where(g=>g.Active).ToArrayAsync());
    }
    private sealed class Interrupted:Exception;
}
