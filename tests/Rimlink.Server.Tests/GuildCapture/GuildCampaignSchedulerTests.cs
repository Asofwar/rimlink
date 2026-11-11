using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildCampaignSchedulerTests
{
    private static GuildCampaignScheduler Scheduler(GuildFixture f) => new(new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Clock,f.Service);
    [Fact]
    public async Task Scheduler_prepares_only_a_due_slot_once_then_expires_it_without_an_offer_or_player_penalty()
    {
        using var f=await GuildFixture.Create(true);await f.EnsureApprovedProfile();
        var target=await f.Place(0);var consent=await f.Service.PreviewConsentAsync(1,50,f.Scope,target.OutpostId,12,default);
        await f.Service.AcceptConsentAsync(1,50,f.Scope,Guid.NewGuid().ToString("D"),target.OutpostId,12,consent.Digest,default);
        f.RegisterExecutor();var scheduled=await f.Service.DeclareCampaignAsync(2,new(f.Scope,Guid.NewGuid().ToString("D"),60,target.OutpostId,1,2),default);
        var scheduler=Scheduler(f);Assert.False(await scheduler.TickAsync(default));
        f.Time.Advance(scheduled.ScheduledAtUtc-f.Time.Now.UtcDateTime);f.RegisterExecutor();
        Assert.True(await scheduler.TickAsync(default));Assert.False(await scheduler.TickAsync(default));
        var battle=await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync();Assert.True(battle.Active);Assert.Null(battle.StartDigest);
        f.Time.Advance(TimeSpan.FromMinutes(5));Assert.True(await scheduler.TickAsync(default));Assert.False(await scheduler.TickAsync(default));
        var campaign=await f.H.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync();Assert.Equal("TechnicalBeforeStart",campaign.TerminalReason);
        battle=await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync();Assert.True(battle.Active);Assert.Equal("CancelledAwaitingCleanup",battle.Phase);
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Fact]
    public async Task An_unprepared_missed_slot_closes_without_inventing_an_assignment_or_cutting_the_old_stock()
    {
        using var f=await GuildFixture.Create(true);await f.EnsureApprovedProfile();
        var target=await f.Place(0);var consent=await f.Service.PreviewConsentAsync(1,50,f.Scope,target.OutpostId,12,default);
        await f.Service.AcceptConsentAsync(1,50,f.Scope,Guid.NewGuid().ToString("D"),target.OutpostId,12,consent.Digest,default);
        f.RegisterExecutor();var scheduled=await f.Service.DeclareCampaignAsync(2,new(f.Scope,Guid.NewGuid().ToString("D"),60,target.OutpostId,1,2),default);
        f.Time.Advance(scheduled.ScheduledAtUtc-f.Time.Now.UtcDateTime+TimeSpan.FromMinutes(5));f.Executors.Revoke();
        Assert.True(await Scheduler(f).TickAsync(default));Assert.Empty(await f.H.Db.Set<GuildBattle>().ToArrayAsync());
        var row=await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();Assert.Equal("Active",row.Phase);Assert.Equal(50,row.AllianceId);Assert.Equal(0,row.Stock);
        Assert.True((await f.Service.SettleAsync(f.Scope,Guid.NewGuid().ToString("D"),target.OutpostId,default)).Stock>=720);
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Fact]
    public async Task Scheduler_never_expires_a_committed_Start()
    {
        using var f=await GuildFixture.Create(true);var(request,_)=await GuildBattleStartTests.Prepare(f,true);await f.Service.StartBattleAsync(request,default);
        f.Time.Advance(TimeSpan.FromMinutes(5));Assert.False(await Scheduler(f).TickAsync(default));
        Assert.Equal("StartCommitted",(await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Phase);
    }
}
