using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildCampaignExpiryTests
{
    private static BattleReadyOfferService Offers(GuildFixture f)=>new(f.Executors,new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Artifacts,f.Profiles,f.Sessions,f.Clock);
    [Theory]
    [InlineData("ready-offer",true)][InlineData("unacknowledged",false)][InlineData("unhealthy",false)][InlineData("already-ready",false)]
    public async Task Only_a_healthy_delivered_current_offer_without_Ready_can_create_the_directed_player_cooldown(string condition,bool penalty)
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,condition=="already-ready");var original=BattleResultStaging.Lease(request.Executor);
        var offers=Offers(f);var offer=await offers.ReadAsync(f.Sessions.ByPlayer(2)!,new(){ClientRequestId=Guid.NewGuid().ToString("D"),Scope=request.Scope.Clone()},default);
        if(condition!="unacknowledged")await offers.AcknowledgeAsync(f.Sessions.ByPlayer(2)!,new(){Scope=request.Scope.Clone(),DeliveryId=offer.DeliveryId,Nonce=offer.Nonce},default);
        var binding=f.Executors.BindingOf(original)!;string boot=f.Executors.Wire(original).ServerBootId;
        f.Time.Advance(TimeSpan.FromMinutes(5));if(condition!="unhealthy")f.Executors.RegisterExecutionAuthenticated(binding,boot);
        string id=Guid.NewGuid().ToString("D");var result=await f.Service.ExpireCampaignAsync(f.Scope,id,start.CampaignId,default);
        Assert.Equal(penalty?"NoAttackerReady":"TechnicalBeforeStart",result.Terminal.Reason);
        Assert.Equal(penalty,result.Terminal.PairCooldownUntilUtc is not null);if(penalty)Assert.Equal(result.Terminal.TerminalAtUtc.AddHours(24),result.Terminal.PairCooldownUntilUtc);
        var battle=await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync();Assert.True(battle.Active);Assert.Equal("CancelledAwaitingCleanup",battle.Phase);
        Assert.Null(battle.StartDigest);Assert.Null(battle.FinalDigest);Assert.Null(battle.StartManifest);Assert.Null(battle.FinalManifest);
        Assert.False((await f.H.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync()).Active);Assert.False((await f.H.Db.Set<GuildBattleSlot>().AsNoTracking().SingleAsync()).Active);
        Assert.False((await f.H.Db.Set<GuildAcquisitionReservation>().AsNoTracking().SingleAsync()).Active);
        var row=await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();Assert.Equal(50,row.AllianceId);Assert.Equal(start.StockAtCut,row.Stock);Assert.Equal(start.RemainderAtCut,row.Remainder);Assert.Equal("Active",row.Phase);
        f.Time.Advance(TimeSpan.FromHours(1));Assert.Equal(result,await f.Service.ExpireCampaignAsync(f.Scope,id,start.CampaignId,default));
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Fact]
    public async Task Expiry_before_the_deadline_or_after_committed_Start_never_cancels_the_campaign()
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,true);
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.ExpireCampaignAsync(f.Scope,Guid.NewGuid().ToString("D"),start.CampaignId,default));
        await f.Service.StartBattleAsync(request,default);f.Time.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.ExpireCampaignAsync(f.Scope,Guid.NewGuid().ToString("D"),start.CampaignId,default));
        string head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() =>
            f.Service.CancelCampaignAsync(Actor.Api, f.Scope, Guid.NewGuid().ToString("D"), start.CampaignId, head, "Post-Start attempt", default));
        Assert.True((await f.H.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync()).Active);
    }
}
