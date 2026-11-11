using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildPreStartCleanupTests
{
    [Theory]
    [InlineData(false, false)][InlineData(true, false)][InlineData(true, true)]
    public async Task PreStart_cancellation_releases_only_after_its_own_fresh_Entry_proof_without_a_fake_Final(bool offerAck, bool manual)
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,false);var old=BattleResultStaging.Lease(request.Executor);
        if(offerAck)
        {
            var offers=new BattleReadyOfferService(f.Executors,new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Artifacts,f.Profiles,f.Sessions,f.Clock);
            var offer=await offers.ReadAsync(f.Sessions.ByPlayer(2)!,new(){ClientRequestId=Guid.NewGuid().ToString("D"),Scope=request.Scope.Clone()},default);
            await offers.AcknowledgeAsync(f.Sessions.ByPlayer(2)!,new(){Scope=request.Scope.Clone(),DeliveryId=offer.DeliveryId,Nonce=offer.Nonce},default);
        }
        var binding=f.Executors.BindingOf(old)!;string boot=f.Executors.Wire(old).ServerBootId;
        if (!manual) f.Time.Advance(TimeSpan.FromMinutes(5));
        var executor=f.Executors.RegisterExecutionAuthenticated(binding,boot);
        string requestId = Guid.NewGuid().ToString("D");
        GuildCampaignExpiryReceipt expired;
        if (manual)
        {
            string head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
            const string reason = "Operator maintenance";
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() =>
                f.Service.CancelCampaignAsync(Actor.Api, f.Scope, requestId, start.CampaignId, Guid.NewGuid().ToString("D"), reason, default));
            Assert.True((await f.H.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync()).Active);
            expired = await f.Service.CancelCampaignAsync(Actor.Api, f.Scope, requestId, start.CampaignId, head, reason, default);
            Assert.Equal("OperatorCancelled", expired.Terminal.Reason);
            Assert.Null(expired.Terminal.PairCooldownUntilUtc);
            Assert.Equal(expired, await f.Service.CancelCampaignAsync(Actor.Api, f.Scope, requestId, start.CampaignId, head, reason, default));
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() =>
                f.Service.CancelCampaignAsync(Actor.Api, f.Scope, requestId, start.CampaignId, head, "Changed reason", default));
            var transition = await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == expired.TransitionId);
            Assert.Equal(head, transition.OldHeadId);
            Assert.Equal(Actor.Api.Name, transition.Actor);
        }
        else expired=await f.Service.ExpireCampaignAsync(f.Scope,requestId,start.CampaignId,default);
        var work=await new BattleExecutorWorkService(f.Executors,new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Clock,f.Artifacts).PollAsync(executor,default);
        Assert.Equal(expired.TransitionId,work.Cancelled.Terminal.TerminalTransitionId);Assert.Null(work.Preparing);
        var challenge=await f.Service.CleanupChallengeAsync(executor,request.Scope,default);
        Assert.Equal(BattleCleanupTerminalKind.PreStartCancellation,challenge.Terminal.Kind);Assert.True(challenge.CommittedFinalDigest.IsEmpty);
        Assert.Equal(expired.TransitionId,challenge.Terminal.TerminalTransitionId);Assert.Equal(start.PreparationTransitionId,challenge.Terminal.AssignmentTransitionId);
        var cleanup=GuildBattleCleanupTests.Request(executor,challenge);var receipt=await f.Service.CleanupBattleAsync(executor,cleanup.ClientRequestId,cleanup.Proof,default);
        Assert.True(receipt.CommittedFinalDigest.IsEmpty);Assert.Equal(challenge.Terminal,receipt.Terminal);Assert.Equal(receipt,await f.Service.CleanupBattleAsync(executor,cleanup.ClientRequestId,cleanup.Proof,default));
        var battle=await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync();Assert.False(battle.Active);Assert.Equal("Cleaned",battle.Phase);Assert.Null(battle.StartDigest);Assert.Null(battle.FinalDigest);
        Assert.Equal(50,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).AllianceId);Assert.Empty(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());Assert.Equal(executor,f.Executors.Available());
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        if(offerAck && !manual)
        {
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(()=>f.Service.DeclareCampaignAsync(2,new(f.Scope,Guid.NewGuid().ToString("D"),60,start.OutpostId,1,2),default));
            var placed=await f.Place(1);var consent=await f.Service.PreviewConsentAsync(2,60,f.Scope,placed.OutpostId,12,default);
            await f.Service.AcceptConsentAsync(2,60,f.Scope,Guid.NewGuid().ToString("D"),placed.OutpostId,12,consent.Digest,default);
            Assert.NotNull(await f.Service.DeclareCampaignAsync(1,new(f.Scope,Guid.NewGuid().ToString("D"),50,placed.OutpostId,1,1),default));
        }
    }
    [Fact]
    public async Task Historical_cleanup_replay_cannot_clear_the_hold_of_a_new_active_assignment_using_the_same_lease()
    {
        using var f=await GuildFixture.Create(true);var(executor,envelope,start)=await GuildBattleResultTests.Prepare(f);
        var applied=await f.Service.ApplyBattleFinalAsync(executor,envelope,default);var challenge=await f.Service.CleanupChallengeAsync(executor,GuildBattleCleanupTests.Scope(applied.Receipt),default);
        var old=GuildBattleCleanupTests.Request(executor,challenge);var receipt=await f.Service.CleanupBattleAsync(executor,old.ClientRequestId,old.Proof,default);
        var next=await f.Service.DeclareCampaignAsync(1,new(f.Scope,Guid.NewGuid().ToString("D"),50,start.OutpostId,2,1),default);
        while(f.Time.Now.UtcDateTime<next.ScheduledAtUtc)
        {
            var remaining=next.ScheduledAtUtc-f.Time.Now.UtcDateTime;f.Time.Advance(remaining<TimeSpan.FromSeconds(20)?remaining:TimeSpan.FromSeconds(20));f.Executors.HeartbeatAuthenticated(executor,false);
        }
        var prepared=await f.Service.PrepareCampaignAsync(f.Scope,Guid.NewGuid().ToString("D"),next.CampaignId,default);f.Executors.HoldCleanup(executor);
        Assert.Equal(receipt,await f.Service.CleanupBattleAsync(executor,old.ClientRequestId,old.Proof,default));Assert.Null(f.Executors.Available());
        Assert.True((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync(b=>b.Id==prepared.BattleId)).Active);
    }
    [Fact]
    public async Task Original_runtime_can_confirm_its_own_Entry_through_historical_profile_RecoveryDelivery()
    {
        using var f=await GuildFixture.Create(true);var(executor,envelope,start)=await GuildBattleResultTests.Prepare(f);var binding=f.Executors.BindingOf(executor)!;
        var applied=await f.Service.ApplyBattleFinalAsync(executor,envelope,default);f.UseExecutorProfile(System.Security.Cryptography.SHA256.HashData("new configured profile"u8));
        binding.Entry=null;binding.Lane=BattleExecutorLane.RecoveryDelivery;binding.BattleId=start.BattleId;binding.Attempt=start.Attempt;binding.Fence=start.Fence;
        binding.StartDigest=ByteString.CopyFrom(Rimlink.Protocol.BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray()));binding.AssignmentTransitionId=start.PreparationTransitionId;
        var hello=new BattleExecutorHelloService(f.Executors,new(f.Time,f.ExecutorBoot),new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Profiles,f.Artifacts,f.Clock);
        var nonce=await hello.BeginAsync(binding,"current credential",default);var lease=await hello.CompleteAsync(new(){Binding=binding,ChallengeId=nonce.ChallengeId,Nonce=nonce.Nonce,ServerBootId=nonce.ServerBootId},"current credential",default);
        var courier=BattleResultStaging.Lease(lease);var challenge=await f.Service.CleanupChallengeAsync(courier,GuildBattleCleanupTests.Scope(applied.Receipt),default);
        var proof=GuildBattleCleanupTests.Request(courier,challenge);var cleaned=await f.Service.CleanupBattleAsync(courier,proof.ClientRequestId,proof.Proof,default);
        Assert.Equal(start.RuntimeInstanceId,cleaned.RuntimeInstanceId);Assert.False((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Active);Assert.Null(f.Executors.Available());
    }
}
