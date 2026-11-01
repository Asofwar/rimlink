using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Social;

public partial class CommunityTests
{
    private static Community.CommunityClient Client(ServerFixture f) => new(f.CreateChannel());
    private static CommunityRequest Request(CommunityAction action, long group = 0, long player = 0, long invite = 0, long silver = 0, string text = "") =>
        new() { Action = action, AllianceId = group, PlayerId = player, InviteId = invite, Silver = silver, Text = text, ClientRequestId = Guid.NewGuid().ToString("N") };
    private static Task<CommunityReceipt> Act(ServerFixture f, LoginReply who, CommunityRequest r) => Client(f).MutateAsync(r, Bearer(who.SessionToken)).ResponseAsync;
    private static Task<CommunityState> Get(ServerFixture f, LoginReply who) => Client(f).GetAsync(new Empty(), Bearer(who.SessionToken)).ResponseAsync;
    private static async Task<long> Group(ServerFixture f, LoginReply owner, LoginReply member)
    {
        long id = (await Act(f, owner, Request(CommunityAction.AllianceCreate, text: "Pioneers"))).AllianceId;
        await Act(f, owner, Request(CommunityAction.AllianceInvite, id, member.Player.PlayerId));
        var invite = Assert.Single((await Get(f, member)).Invitations);
        await Act(f, member, Request(CommunityAction.InviteAccept, invite: invite.Id));
        return id;
    }

    [Fact]
    public async Task ConsentReplayAndIntentBinding()
    {
        using var f = EconomyServer();
        var owner = await f.RegisterAndLogin("owner", playerName: "Owner");
        var member = await f.RegisterAndLogin("member", playerName: "Member");
        var create = Request(CommunityAction.AllianceCreate, text: "Pioneers");
        var group = await Act(f, owner, create);
        Assert.Equal(group, await Act(f, owner, create));
        create.Text = "Another";
        Assert.Equal(ErrorCode.CommunityIntent, (await Assert.ThrowsAsync<RpcException>(() => Act(f, owner, create))).ErrorDetail()?.Code);
        await Act(f, owner, Request(CommunityAction.AllianceInvite, group.AllianceId, member.Player.PlayerId));
        Assert.Null((await Get(f, member)).Alliance);
        var invite = Assert.Single((await Get(f, member)).Invitations);
        await Act(f, member, Request(CommunityAction.InviteAccept, invite: invite.Id));
        Assert.Equal(2, (await Get(f, member)).Alliance.Members.Count);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, owner, Request(CommunityAction.AllianceLeave, group.AllianceId)));
    }

    [Fact]
    public async Task TreasuryPermissionDailyCapReplayAndRefundConserveSilver()
    {
        using var f = EconomyServer();
        var owner = await f.RegisterAndLogin("owner");
        var member = await f.RegisterAndLogin("member");
        long id = await Group(f, owner, member);
        await Fund(f, owner.Player.PlayerId, 1000);
        var deposit = Request(CommunityAction.TreasuryDeposit, id, silver: 500);
        await Act(f, owner, deposit);
        await Act(f, owner, deposit);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, member, Request(CommunityAction.TreasuryWithdraw, id, silver: 1)));
        var rights = Request(CommunityAction.TreasuryPermission, id, member.Player.PlayerId);
        rights.MayWithdraw = true; rights.DailyCap = 100;
        await Act(f, owner, rights);
        var withdraw = Request(CommunityAction.TreasuryWithdraw, id, silver: 60);
        await Act(f, member, withdraw); await Act(f, member, withdraw);
        Assert.Equal(ErrorCode.TreasuryLimit, (await Assert.ThrowsAsync<RpcException>(() => Act(f, member, Request(CommunityAction.TreasuryWithdraw, id, silver: 41)))).ErrorDetail()?.Code);
        using (var scope = f.Services.CreateScope())
        {
            var wealth = scope.ServiceProvider.GetRequiredService<ServerWealth>();
            Assert.Equal(940, await wealth.Of(owner.Player.PlayerId));
            Assert.Equal(60, await wealth.Of(member.Player.PlayerId));
        }
        var disband = Request(CommunityAction.AllianceDisband, id);
        await Act(f, owner, disband); await Act(f, owner, disband);
        Assert.Null((await Get(f, member)).Alliance);
        Assert.Equal(940, (await Clients(f, owner.SessionToken).Wallet.GetAsync(new Empty(), Bearer(owner.SessionToken))).Balance);
        Assert.Equal(60, (await Clients(f, member.SessionToken).Wallet.GetAsync(new Empty(), Bearer(member.SessionToken))).Balance);
    }

    [Fact]
    public async Task PrivateChatNeverEntersPublicHistoryAndFormerMembersLoseHistory()
    {
        using var f = EconomyServer();
        var owner = await f.RegisterAndLogin("owner");
        var member = await f.RegisterAndLogin("member");
        var stranger = await f.RegisterAndLogin("stranger");
        long id = await Group(f, owner, member);
        await Act(f, owner, Request(CommunityAction.AllianceChat, id, text: "only allies"));
        Assert.Equal("only allies", Assert.Single((await Get(f, member)).Chat).Text);
        Assert.Empty((await Get(f, stranger)).Chat);
        var publicChat = new Protocol.V1.Chat.ChatClient(f.CreateChannel());
        Assert.DoesNotContain((await publicChat.HistoryAsync(new Empty(), Bearer(stranger.SessionToken))).Messages, m => m.Text == "only allies");
        await Act(f, member, Request(CommunityAction.AllianceLeave, id));
        Assert.Empty((await Get(f, member)).Chat);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, member, Request(CommunityAction.AllianceChat, id, text: "late")));
    }

    [Fact]
    public async Task FriendsNeedConsentAndRevokeOrBlockInvalidatesInvitations()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        await Act(f, a, Request(CommunityAction.FriendInvite, player: b.Player.PlayerId));
        Assert.Empty((await Get(f, a)).Friends);
        long invite = Assert.Single((await Get(f, b)).Invitations).Id;
        await Act(f, a, Request(CommunityAction.InviteRevoke, invite: invite));
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.InviteAccept, invite: invite)));
        await Act(f, a, Request(CommunityAction.FriendInvite, player: b.Player.PlayerId));
        invite = Assert.Single((await Get(f, b)).Invitations).Id;
        await SocialFixtures.Social(f, b.SessionToken).Social.BlockAsync(a.Player, Bearer(b.SessionToken));
        Assert.Empty((await Get(f, b)).Invitations);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.InviteAccept, invite: invite)));
        await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.FriendInvite, player: b.Player.PlayerId)));
    }
}
