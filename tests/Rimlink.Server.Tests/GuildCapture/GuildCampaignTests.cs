using Microsoft.EntityFrameworkCore;
using Grpc.Core;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildCampaignTests
{
    [Fact]
    public async Task Preparation_cuts_old_stock_once_and_does_not_transfer_ownership_or_start_simulation()
    {
        using var f = await GuildFixture.Create(true); var target = await Owned(f);
        f.RegisterExecutor();
        var scheduled = await f.Service.DeclareCampaignAsync(2, Request(f, target), default);
        f.Time.Advance(scheduled.ScheduledAtUtc - f.Time.Now.UtcDateTime);
        f.RegisterExecutor();
        string request = Guid.NewGuid().ToString("D");
        var prepared = await f.Service.PrepareCampaignAsync(f.Scope, request, scheduled.CampaignId, default);
        Assert.Equal(prepared, await f.Service.PrepareCampaignAsync(f.Scope, request, scheduled.CampaignId, default));
        var row = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal(720, row.Stock); Assert.Equal(50, row.AllianceId); Assert.Equal(1, row.Revision); Assert.Equal("PreparingBattle", row.Phase);
        var battle = await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync();
        Assert.Null(battle.StartDigest); Assert.Null(battle.StartedAtUtc); Assert.Equal("Prepared", battle.Phase);
        Assert.Empty(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(720, (await f.Service.SettleAsync(f.Scope, Guid.NewGuid().ToString("D"), target.OutpostId, default)).Stock);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("early")][InlineData("missed")][InlineData("executor")]
    public async Task Preparation_refuses_early_missed_or_unhealthy_slots_without_guessing_a_winner(string fault)
    {
        using var f = await GuildFixture.Create(true); var target = await Owned(f);
        f.RegisterExecutor();
        var scheduled = await f.Service.DeclareCampaignAsync(2, Request(f, target), default);
        if (fault != "early") f.Time.Advance(scheduled.ScheduledAtUtc - f.Time.Now.UtcDateTime + (fault == "missed" ? TimeSpan.FromMinutes(6) : TimeSpan.Zero));
        if (fault == "missed") f.RegisterExecutor();
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.PrepareCampaignAsync(f.Scope, Guid.NewGuid().ToString("D"), scheduled.CampaignId, default));
        Assert.Empty(await f.H.Db.Set<GuildBattle>().ToArrayAsync());
        Assert.Equal("Active", (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Phase);
    }
    [Fact]
    public async Task Declaration_atomically_reserves_a_real_slot_and_one_acquisition_without_cutting_production()
    {
        using var f = await GuildFixture.Create(true); var target = await Owned(f);
        f.RegisterExecutor(); var request = Request(f, target);
        var first = await f.Service.DeclareCampaignAsync(2, request, default);
        Assert.Equal(first, await f.Service.DeclareCampaignAsync(2, request, default));
        f.Executors.Revoke();
        Assert.Equal(first, await f.Service.DeclareCampaignAsync(2, request, default));
        var campaign = await f.H.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync();
        var slot = await f.H.Db.Set<GuildBattleSlot>().AsNoTracking().SingleAsync();
        Assert.True(campaign.Active); Assert.Equal("Scheduled", campaign.Phase); Assert.Null(campaign.ProductionCutUtc);
        Assert.True(slot.Active); Assert.Equal(TimeSpan.FromMinutes(30), slot.EndsAtUtc - slot.StartsAtUtc);
        Assert.Equal("arbiter-1", slot.ExecutorId); Assert.True((await f.H.Db.Set<GuildAcquisitionReservation>().SingleAsync()).Active);
        Assert.Equal(50, (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).AllianceId);
        Assert.Equal("Active", (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Phase);
        Assert.True(slot.StartsAtUtc >= f.Time.Now.UtcDateTime.AddHours(24));
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("executor")][InlineData("revision")][InlineData("self")][InlineData("commander")][InlineData("freeze")]
    public async Task Refused_declarations_leave_no_campaign_or_reservations(string fault)
    {
        using var f = await GuildFixture.Create(true); var target = await Owned(f); var request = Request(f, target);
        if (fault != "executor") f.RegisterExecutor();
        if (fault == "revision") request = request with { OwnerRevision = 2 };
        if (fault == "self") request = request with { AllianceId = 50 };
        if (fault == "commander") request = request with { CommanderId = 1 };
        if (fault == "freeze")
        {
            var player = await f.H.Db.Players.SingleAsync(p => p.Id == 2); player.EconomyFrozenAt = f.Time.Now.UtcDateTime;
            await f.H.Db.SaveChangesAsync(); f.H.Db.ChangeTracker.Clear();
        }
        var error = await Record.ExceptionAsync(() => f.Service.DeclareCampaignAsync(2, request, default));
        Assert.True(error is OutpostEvidenceUnavailableException or RpcException, error?.ToString() ?? "the invalid declaration was accepted");
        Assert.Empty(await f.H.Db.Set<GuildCampaign>().ToArrayAsync());
        Assert.Empty(await f.H.Db.Set<GuildBattleSlot>().ToArrayAsync());
        Assert.Empty(await f.H.Db.Set<GuildAcquisitionReservation>().ToArrayAsync());
    }

    [Fact]
    public async Task Changed_intent_and_second_active_attack_do_not_create_another_booking()
    {
        using var f = await GuildFixture.Create(true); var target = await Owned(f);
        f.RegisterExecutor(); var request = Request(f, target);
        await f.Service.DeclareCampaignAsync(2, request, default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.DeclareCampaignAsync(2, request with { CommanderId = 1 }, default));
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.DeclareCampaignAsync(2, request with { RequestId = Guid.NewGuid().ToString("D") }, default));
        Assert.Single(await f.H.Db.Set<GuildBattleSlot>().ToArrayAsync());
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Declaration_recovers_once_across_external_anchor_crashes(string point, bool committed)
    {
        using var f = await GuildFixture.Create(true); var target = await Owned(f);
        f.RegisterExecutor(); var request = Request(f, target);
        f.H.Authority.CommitCheckpoint = p => { if (p == point) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => f.Service.DeclareCampaignAsync(2, request, default));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed ? 1 : 0, await f.H.Db.Set<GuildCampaign>().CountAsync());
        await f.Service.DeclareCampaignAsync(2, request, default);
        Assert.Single(await f.H.Db.Set<GuildCampaign>().ToArrayAsync()); Assert.Single(await f.H.Db.Set<GuildBattleSlot>().ToArrayAsync());
    }

    [Theory]
    [InlineData("normal",true)][InlineData("peaceful",false)][InlineData("off-delay",false)]
    public async Task A_separate_peaceful_attacking_commander_cannot_borrow_the_leaders_eligibility(string mode,bool allowed)
    {
        using var f=await GuildFixture.Create(true,true);var target=await Owned(f);
        var player=await f.H.Db.Players.SingleAsync(p=>p.Id==3);player.Peaceful=mode=="peaceful";
        player.PeacefulOffAt=mode=="off-delay"?f.Time.Now.UtcDateTime.AddDays(1):null;
        await f.H.Db.SaveChangesAsync();f.H.Db.ChangeTracker.Clear();f.RegisterExecutor();
        if(allowed)Assert.NotNull(await f.Service.DeclareCampaignAsync(2,Request(f,target) with{CommanderId=3},default));
        else
        {
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(()=>f.Service.DeclareCampaignAsync(2,Request(f,target) with{CommanderId=3},default));
            Assert.Empty(await f.H.Db.Set<GuildCampaign>().ToArrayAsync());Assert.Empty(await f.H.Db.Set<GuildBattleSlot>().ToArrayAsync());
        }
    }
    private static GuildCampaignRequest Request(GuildFixture f, GuildOutpostResult target) =>
        new(f.Scope, Guid.NewGuid().ToString("D"), 60, target.OutpostId, 1, 2);
    private static async Task<GuildOutpostResult> Owned(GuildFixture f)
    {
        await f.EnsureApprovedProfile();
        var target = await f.Place(0); var consent = await f.Service.PreviewConsentAsync(1, 50, f.Scope, target.OutpostId, 12, default);
        return await f.Service.AcceptConsentAsync(1, 50, f.Scope, Guid.NewGuid().ToString("D"), target.OutpostId, 12, consent.Digest, default);
    }
    private sealed class Interrupted : Exception;
}
