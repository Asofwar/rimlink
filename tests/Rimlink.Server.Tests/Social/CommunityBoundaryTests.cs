using System.Xml.Linq;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Social;

public partial class CommunityTests
{
    [Fact]
    public async Task PublicInviteGateChecksSelfBlockAndPreferencesForColonyCallers()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        using (var scope = f.Services.CreateScope())
        {
            var gate = scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Social.AllianceService>();
            await gate.RequireMayInvite(a.Player.PlayerId, b.Player.PlayerId);
            await Assert.ThrowsAsync<RpcException>(() => gate.RequireMayInvite(a.Player.PlayerId, a.Player.PlayerId));
            await Assert.ThrowsAsync<RpcException>(() => gate.RequireMayInvite(a.Player.PlayerId, 9999));
        }
        await Act(f, b, new CommunityRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Action = CommunityAction.ProfileUpdate, InvitationsOpen = false });
        using (var scope = f.Services.CreateScope())
        {
            var gate = scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Social.AllianceService>();
            Assert.False((await gate.Profile(b.Player.PlayerId)).InvitationsOpen);
            Assert.Equal(ErrorCode.AllianceRefused, (await Assert.ThrowsAsync<RpcException>(() => gate.RequireMayInvite(a.Player.PlayerId, b.Player.PlayerId))).ErrorDetail()?.Code);
        }
        await Act(f, b, new CommunityRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Action = CommunityAction.ProfileUpdate, InvitationsOpen = true });
        await SocialFixtures.Social(f, b.SessionToken).Social.BlockAsync(a.Player, Bearer(b.SessionToken));
        using var blocked = f.Services.CreateScope();
        var blockedGate = blocked.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Social.AllianceService>();
        Assert.Equal(ErrorCode.Blocked, (await Assert.ThrowsAsync<RpcException>(() => blockedGate.RequireMayInvite(a.Player.PlayerId, b.Player.PlayerId))).ErrorDetail()?.Code);
        Assert.Equal(ErrorCode.Blocked, (await Assert.ThrowsAsync<RpcException>(() => blockedGate.RequireMayInvite(b.Player.PlayerId, a.Player.PlayerId))).ErrorDetail()?.Code);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("bobby")]
    public async Task SelfRenameRefusalsHaveCommunityTranslationKeys(string name)
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); await f.RegisterAndLogin("bobby");
        var ex = await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.ProfileRename, text: name)));
        Assert.Equal("Community.Rename", ex.ErrorDetail()?.Key);
        Assert.Equal("alice", (await Get(f, a)).Profile.Name);
    }

    [Fact]
    public async Task DailyAllowanceRollsOverAtUtcMidnight()
    {
        var time = new ManualTimeProvider();
        using var f = EconomyServer().WithServices(s => s.AddSingleton<TimeProvider>(time));
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long id = await Group(f, a, b); await Fund(f, a.Player.PlayerId, 500);
        await Act(f, a, Request(CommunityAction.TreasuryDeposit, id, silver: 500));
        var rights = Request(CommunityAction.TreasuryPermission, id, b.Player.PlayerId); rights.MayWithdraw = true; rights.DailyCap = 100;
        await Act(f, a, rights); await Act(f, b, Request(CommunityAction.TreasuryWithdraw, id, silver: 100));
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.TreasuryWithdraw, id, silver: 1)));
        time.Now = new DateTimeOffset(time.Now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        Assert.Equal(0, (await Get(f, b)).Alliance.Members.Single(m => m.Player.PlayerId == b.Player.PlayerId).WithdrawnToday);
        await Act(f, b, Request(CommunityAction.TreasuryWithdraw, id, silver: 100));
        Assert.Equal(300, (await Get(f, a)).Alliance.Silver);
    }

    [Fact]
    public async Task ExpiredOrRefusedInvitationCannotBeAccepted()
    {
        var time = new ManualTimeProvider();
        using var f = EconomyServer().WithServices(s => s.AddSingleton<TimeProvider>(time));
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        await Act(f, a, Request(CommunityAction.FriendInvite, player: b.Player.PlayerId));
        long id = Assert.Single((await Get(f, b)).Invitations).Id;
        time.Advance(TimeSpan.FromDays(8));
        Assert.Empty((await Get(f, b)).Invitations);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.InviteAccept, invite: id)));
        await Act(f, a, Request(CommunityAction.FriendInvite, player: b.Player.PlayerId));
        id = Assert.Single((await Get(f, b)).Invitations).Id;
        await Act(f, b, Request(CommunityAction.InviteRefuse, invite: id));
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.InviteAccept, invite: id)));
    }

    [Fact]
    public async Task ConcurrentAcceptOfTwoAlliancesHasOnlyOneMembership()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby"); var c = await f.RegisterAndLogin("carol");
        long first = (await Act(f, a, Request(CommunityAction.AllianceCreate, text: "First"))).AllianceId;
        long second = (await Act(f, b, Request(CommunityAction.AllianceCreate, text: "Second"))).AllianceId;
        await Act(f, a, Request(CommunityAction.AllianceInvite, first, c.Player.PlayerId));
        await Act(f, b, Request(CommunityAction.AllianceInvite, second, c.Player.PlayerId));
        var invites = (await Get(f, c)).Invitations.ToArray();
        async Task<bool> Accept(long id)
        {
            try { await Act(f, c, Request(CommunityAction.InviteAccept, invite: id)); return true; }
            catch (RpcException ex) when (ex.ErrorDetail()?.Code == ErrorCode.AllianceRefused) { return false; }
        }
        Assert.Single(await Task.WhenAll(invites.Select(i => Accept(i.Id))), accepted => accepted);
        using var scope = f.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Set<AllianceMember>().Where(m => m.PlayerId == c.Player.PlayerId).ToListAsync());
    }

    [Fact]
    public async Task ConcurrentExactReplayDebitsOnceAndRequestIdsAreBoundToCaller()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        var create = Request(CommunityAction.AllianceCreate, text: "First");
        long first = (await Act(f, a, create)).AllianceId;
        create.Text = "Second";
        long second = (await Act(f, b, create)).AllianceId;
        Assert.NotEqual(first, second);
        await Fund(f, a.Player.PlayerId, 100);
        var deposit = Request(CommunityAction.TreasuryDeposit, first, silver: 100);
        var results = await Task.WhenAll(Act(f, a, deposit), Act(f, a, deposit));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(100, (await Get(f, a)).Alliance.Silver);
        using var scope = f.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().WalletEntries.Where(e => e.Kind == "alliance-deposit").ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad/id")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task MissingOrMalformedRequestIdIsRejected(string requestId)
    {
        using var f = EconomyServer(); var a = await f.RegisterAndLogin("alice");
        var r = Request(CommunityAction.AllianceCreate, text: "Pioneers"); r.ClientRequestId = requestId;
        Assert.Equal(ErrorCode.CommunityIntent, (await Assert.ThrowsAsync<RpcException>(() => Act(f, a, r))).ErrorDetail()?.Code);
        Assert.Null((await Get(f, a)).Alliance);
    }

    [Fact]
    public async Task OversizedOrMarkupProfileDoesNotChangePublicFields()
    {
        using var f = EconomyServer(); var a = await f.RegisterAndLogin("alice");
        await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.ProfileUpdate, text: new string('x', 501))));
        await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.ProfileUpdate, text: "<color=red>red</color>")));
        Assert.Empty((await Get(f, a)).Profile.Bio);
    }

    [Fact]
    public void CommunityRefusalKeysHaveBothTranslationsWithMatchingPlaceholders()
    {
        var english = XDocument.Load(Path.Combine(RepoPaths.Root, "mod/Languages/English/Keyed/Community.xml")).Root!.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
        var russian = XDocument.Load(Path.Combine(RepoPaths.Root, "mod/Languages/Russian/Keyed/Community.xml")).Root!.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
        Assert.Equal(english.Keys.Order(), russian.Keys.Order());
        foreach (var field in typeof(RefusalKeys).GetFields().Where(f => f.Name.StartsWith("Community", StringComparison.Ordinal)))
        {
            string key = "Rimlink.Refusal." + (string)field.GetRawConstantValue()!;
            Assert.True(english.ContainsKey(key), key);
            Assert.True(russian.ContainsKey(key), key);
        }
        foreach (string key in english.Keys)
            Assert.Equal(System.Text.RegularExpressions.Regex.Matches(english[key], @"\{\d+\}").Select(m => m.Value).Order(),
                System.Text.RegularExpressions.Regex.Matches(russian[key], @"\{\d+\}").Select(m => m.Value).Order());
    }
}
