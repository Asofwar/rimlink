using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Influence;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.Social;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildGovernanceTests
{
    [Fact]
    public async Task Creating_a_guild_after_V3_commits_governance_and_replays_the_same_receipt()
    {
        using var f = await GuildFixture.Create(); var social = Social(f);
        var request = Request(CommunityAction.AllianceCreate); request.Text = "second guild";
        var first = await social.Mutate(2, request);
        Assert.Equal(first, await social.Mutate(2, request));
        Assert.True(first.AllianceId > 0);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
        Assert.Single(await f.H.Db.OutpostTransitions.Where(t => t.Kind == "GuildGovernance").ToArrayAsync());
    }

    [Fact]
    public async Task Join_and_lead_transfer_preserve_object_identity_and_accepted_risk()
    {
        using var f = await GuildFixture.Create(); var social = Social(f); var placed = await f.Place(0);
        var terms = await f.Service.PreviewConsentAsync(1, 50, f.Scope, placed.OutpostId, 4, default);
        await f.Service.AcceptConsentAsync(1, 50, f.Scope, Guid.NewGuid().ToString("D"), placed.OutpostId, 4, terms.Digest, default);
        var invite = Request(CommunityAction.AllianceInvite); invite.AllianceId = 50; invite.PlayerId = 2;
        await social.Mutate(1, invite);
        long invitation = await f.H.Db.Set<CommunityInvitation>().Select(i => i.Id).SingleAsync();
        var join = Request(CommunityAction.InviteAccept); join.InviteId = invitation; await social.Mutate(2, join);
        var transfer = Request(CommunityAction.AllianceTransfer); transfer.AllianceId = 50; transfer.PlayerId = 2;
        await social.Mutate(1, transfer);
        var row = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal(50, row.AllianceId); Assert.Equal(1, row.Revision); Assert.Equal(4, row.DefenceHourUtc);
        Assert.Equal(2, (await f.H.Db.Set<AllianceGroup>().AsNoTracking().SingleAsync()).OwnerId);
        Assert.Equal(1, (await f.H.Db.Set<GuildCaptureConsent>().AsNoTracking().SingleAsync()).LeaderId);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Disband_with_unresolved_guild_assets_refuses_before_any_refund_or_membership_effect()
    {
        using var f = await GuildFixture.Create(); var placed = await f.Place(0);
        var terms = await f.Service.PreviewConsentAsync(1, 50, f.Scope, placed.OutpostId, 4, default);
        await f.Service.AcceptConsentAsync(1, 50, f.Scope, Guid.NewGuid().ToString("D"), placed.OutpostId, 4, terms.Digest, default);
        string head = await f.H.Db.OutpostAuthority.Select(a => a.HeadId).SingleAsync();
        var request = Request(CommunityAction.AllianceDisband); request.AllianceId = 50;
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => Social(f).Mutate(1, request));
        Assert.False((await f.H.Db.Set<AllianceGroup>().AsNoTracking().SingleAsync()).Disbanded);
        Assert.Single(await f.H.Db.Set<AllianceMember>().ToArrayAsync());
        Assert.Equal(head, await f.H.Db.OutpostAuthority.Select(a => a.HeadId).SingleAsync());
    }

    private static CommunityRequest Request(CommunityAction action) => new() { Action = action, ClientRequestId = Guid.NewGuid().ToString("N") };
    private static AllianceService Social(GuildFixture f)
    {
        var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        var options = Options.Create(new EconomyOptions());
        return new(f.H.Db, new WalletService(f.H.Db, events), new EconomyGate(options, new FreezeState(), new PlayerFreeze(f.H.Db, events)),
            new EconomyGuard(f.H.Db, options), events, f.Time, new ServiceCollection()
                .AddSingleton(new GuildLifecycle(f.H.Db, f.H.Authority, f.H.Files, new LiveStateLock())).BuildServiceProvider(),
            new InfluenceService(f.H.Db, events, new InfluenceProcessEpoch(), null!));
    }
}
