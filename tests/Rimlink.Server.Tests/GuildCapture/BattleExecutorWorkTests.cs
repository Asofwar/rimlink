using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleExecutorWorkTests
{
    [Fact]
    public async Task Prepared_work_is_bound_to_the_current_executor_and_never_commits_a_financial_transition_on_poll()
    {
        using var f = await GuildFixture.Create(true);
        var (executor, prepared) = await Prepare(f);
        var reader = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, new BattleStateLock());
        var service = new BattleExecutorWorkService(f.Executors, reader, f.Clock, new BattleArtifactStore(f.H.Data.Archive));
        string head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        using (await f.H.Anchor.AcquireAsync(default))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = await service.PollAsync(executor, deadline.Token);
            Assert.Equal(first, await service.PollAsync(executor, deadline.Token));
            Assert.Equal(prepared.BattleId, first.Preparing.Scope.BattleId);
            Assert.Equal(f.Scope.Boundary, first.Preparing.Scope.RestoreBoundary);
            Assert.Equal(Convert.FromHexString(prepared.StartNonce), first.Preparing.StartNonce.ToByteArray());
        }
        Assert.Equal(head, (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Null((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).StartedAtUtc);
    }

    internal static async Task<(BattleExecutorLease Executor, GuildBattlePreparationReceipt Prepared)> Prepare(GuildFixture f)
    {
        await f.EnsureApprovedProfile();
        var target = await f.Place(0); var consent = await f.Service.PreviewConsentAsync(1, 50, f.Scope, target.OutpostId, 12, default);
        await f.Service.AcceptConsentAsync(1, 50, f.Scope, Guid.NewGuid().ToString("D"), target.OutpostId, 12, consent.Digest, default);
        f.RegisterExecutor();
        var campaign = await f.Service.DeclareCampaignAsync(2, new(f.Scope, Guid.NewGuid().ToString("D"), 60, target.OutpostId, 1, 2), default);
        f.Time.Advance(campaign.ScheduledAtUtc - f.Time.Now.UtcDateTime);
        var executor = f.RegisterExecutor();
        var prepared = await f.Service.PrepareCampaignAsync(f.Scope, Guid.NewGuid().ToString("D"), campaign.CampaignId, default);
        return (executor, prepared);
    }

    [Fact]
    public async Task An_idle_executor_gets_no_fake_battle_and_a_foreign_lease_is_refused()
    {
        using var f = await GuildFixture.Create(); var executor = f.RegisterExecutor();
        var reader = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, new BattleStateLock());
        var service = new BattleExecutorWorkService(f.Executors, reader, f.Clock, new BattleArtifactStore(f.H.Data.Archive));
        Assert.Null((await service.PollAsync(executor, default)).Preparing);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PollAsync(executor with { LeaseId = Guid.NewGuid().ToString("D") }, default));
    }
}
