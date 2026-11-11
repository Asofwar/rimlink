using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildCaptureFreezeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Freeze_and_final_obey_the_common_commit_order(bool finalFirst)
    {
        using var f = await GuildFixture.Create(twoGuilds: true);
        var (executor, envelope, _) = await GuildBattleResultTests.Prepare(f);
        var applied = finalFirst ? await f.Service.ApplyBattleFinalAsync(executor, envelope, default) : null;
        var command = await Command(f, true);
        var freeze = await f.Service.SetCaptureFrozenAsync(Actor.Api, command, default);
        Assert.Equal(freeze, await f.Service.SetCaptureFrozenAsync(Actor.Api, command, default));
        Assert.Equal(1, freeze.Revision);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(freeze, await GuildAuthorityEvidence.LatestFreezeAsync(f.H.Db, f.Scope.WorldId, default));
        Assert.False(await f.H.Db.Set<GuildBattleControlGrant>().AnyAsync(g => g.Active));
        if (finalFirst)
            Assert.Equal(applied, await f.Service.ApplyBattleFinalAsync(executor, envelope, default));
        else
        {
            var blocked = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() =>
                f.Service.ApplyBattleFinalAsync(executor, envelope, default));
            Assert.Equal("guild-capture-frozen", blocked.Message);
            var unfreeze = await Command(f, false);
            var pending = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() =>
                f.Service.SetCaptureFrozenAsync(Actor.Api, unfreeze, default));
            Assert.Equal("guild-freeze-active-campaigns", pending.Message);
        }
        var row = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal(finalFirst ? 60 : 50, row.AllianceId);
        Assert.Equal(finalFirst ? 2 : 1, row.Revision);
        Assert.Equal(finalFirst, (await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).FinalDigest is not null);
    }

    [Fact]
    public async Task Started_abort_preserves_the_start_and_holds_the_arena_until_the_original_executor_proves_entry()
    {
        using var f = await GuildFixture.Create(twoGuilds: true);
        var (executor, envelope, start) = await GuildBattleResultTests.Prepare(f);
        var before = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        await f.Service.SetCaptureFrozenAsync(Actor.Api, await Command(f, true), default);
        var frozen = await Command(f, false);
        var command = new GuildStartedAbortCommand(f.Scope, Guid.NewGuid().ToString("D"), frozen.ExpectedHeadId, start.BattleId, "Operator stop");
        var aborted = await f.Service.AbortStartedBattleAsync(Actor.Api, command, default);
        Assert.Equal(aborted, await f.Service.AbortStartedBattleAsync(Actor.Api, command, default));
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.ApplyBattleFinalAsync(executor, envelope, default));
        var battle = await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync();
        Assert.Equal("CancelledAwaitingCleanup", battle.Phase);
        Assert.True(battle.Active);
        Assert.Null(battle.FinalDigest);
        Assert.NotNull(battle.StartDigest);
        Assert.Null(f.Executors.Available());
        var row = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal((before.AllianceId, before.Revision, before.Stock, before.Remainder), (row.AllianceId, row.Revision, row.Stock, row.Remainder));
        Assert.Empty(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());

        var scope = new BattleScopeMessage { WorldInstanceId = battle.WorldId, RestoreBoundary = battle.Boundary,
            BattleId = battle.Id, Attempt = battle.Attempt, Fence = battle.Fence };
        var transport = BattleRuntimeStartedTests.Transport(f);
        var work = await transport.PollAsync(executor, scope, default);
        Assert.Equal(BattleCleanupTerminalKind.StartedAbort, work.Aborted.Terminal.Kind);
        Assert.Equal(aborted.TransitionId, work.Aborted.Terminal.TerminalTransitionId);
        Assert.Equal(battle.StartDigest, work.Aborted.Terminal.AbortedStartDigest.ToByteArray());
        Assert.Empty(work.Commands);
        var view = await transport.ReadCompletedViewAsync(f.Sessions.ByPlayer(2)!,
            new() { Scope = scope, StartDigest = work.StartDigest }, default);
        Assert.Equal(work.Aborted.Terminal, view.Abort);
        Assert.Null(view.Receipt);
        var challenge = await f.Service.CleanupChallengeAsync(executor, scope, default);
        Assert.Equal(work.Aborted.Terminal, challenge.Terminal);
        var proof = GuildBattleCleanupTests.Request(executor, challenge);
        var cleaned = await f.Service.CleanupBattleAsync(executor, proof.ClientRequestId, proof.Proof, default);
        Assert.Equal(challenge.Terminal, cleaned.Terminal);
        Assert.False((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Active);
        Assert.Equal(executor, f.Executors.Available());
        await f.Service.SetCaptureFrozenAsync(Actor.Api, await Command(f, false), default);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Freeze_closes_a_future_campaign_without_penalty_before_unfreeze_allows_a_new_booking()
    {
        using var f = await GuildFixture.Create(twoGuilds: true);
        await f.EnsureApprovedProfile();
        var target = await f.Place(0);
        var consent = await f.Service.PreviewConsentAsync(1, 50, f.Scope, target.OutpostId, 12, default);
        await f.Service.AcceptConsentAsync(1, 50, f.Scope, Guid.NewGuid().ToString("D"), target.OutpostId, 12, consent.Digest, default);
        f.RegisterExecutor();
        var request = new GuildCampaignRequest(f.Scope, Guid.NewGuid().ToString("D"), 60, target.OutpostId, 1, 2);
        var scheduled = await f.Service.DeclareCampaignAsync(2, request, default);

        await f.Service.SetCaptureFrozenAsync(Actor.Api, await Command(f, true), default);
        var next = request with { RequestId = Guid.NewGuid().ToString("D") };
        var blocked = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.DeclareCampaignAsync(2, next, default));
        Assert.Equal("guild-capture-frozen", blocked.Message);
        Assert.True(f.Time.Now.UtcDateTime < scheduled.ScheduledAtUtc);
        var scheduler = new GuildCampaignScheduler(new(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, f.Battles), f.Clock, f.Service);
        Assert.True(await scheduler.TickAsync(default));
        Assert.False(await scheduler.TickAsync(default));
        Assert.Empty(await f.H.Db.Set<GuildBattle>().ToArrayAsync());
        Assert.False(await f.H.Db.Set<GuildBattleSlot>().AnyAsync(s => s.Active));
        Assert.False(await f.H.Db.Set<GuildAcquisitionReservation>().AnyAsync(r => r.Active));
        var terminal = await f.H.Db.Set<GuildCaptureOperation>().SingleAsync(o => o.Kind == GuildOperatorCancellation.Kind);
        Assert.Null(GuildAuthorityEvidence.ReadExpiry(terminal.Receipt).Terminal.PairCooldownUntilUtc);

        var opened = await f.Service.SetCaptureFrozenAsync(Actor.Api, await Command(f, false), default);
        Assert.Equal(2, opened.Revision);
        Assert.False(opened.Command.Frozen);
        Assert.NotEqual(scheduled.CampaignId, (await f.Service.DeclareCampaignAsync(2, next, default)).CampaignId);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    private static async Task<GuildFreezeCommand> Command(GuildFixture f, bool frozen)
    {
        string head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        return new(f.Scope, Guid.NewGuid().ToString("D"), head, frozen, "Operator maintenance");
    }
}
