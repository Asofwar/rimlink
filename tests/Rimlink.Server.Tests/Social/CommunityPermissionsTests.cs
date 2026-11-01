using System.Net;
using System.Text.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Social;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using static Rimlink.Server.Tests.Support.LiveFixtures;

namespace Rimlink.Server.Tests.Social;

public partial class CommunityTests
{
    [Fact]
    public async Task ActiveLiveRaidStillPreventsJoiningAfterItsRaidRowFinishes()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long group = (await Act(f, a, Request(CommunityAction.AllianceCreate, text: "Pioneers"))).AllianceId;
        await Act(f, a, Request(CommunityAction.AllianceInvite, group, b.Player.PlayerId));
        long invite = Assert.Single((await Get(f, b)).Invitations).Id;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            db.LiveSessions.Add(new Rimlink.Server.Storage.Entities.LiveSession { HostPlayerId = a.Player.PlayerId, GuestPlayerId = b.Player.PlayerId, Kind = (int)LiveKind.LiveRaid, State = LiveStateDb.Ending, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.InviteAccept, invite: invite)));
        Assert.Null((await Get(f, b)).Alliance);
    }

    [Fact]
    public async Task FriendsAcceptRemoveAndInvitationPreferencesArePersisted()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        await Act(f, a, Request(CommunityAction.FriendInvite, player: b.Player.PlayerId));
        long id = Assert.Single((await Get(f, b)).Invitations).Id;
        await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.InviteAccept, invite: id)));
        await Act(f, b, Request(CommunityAction.InviteAccept, invite: id));
        Assert.Equal(a.Player.PlayerId, Assert.Single((await Get(f, b)).Friends).PlayerId);
        Assert.Equal(b.Player.PlayerId, Assert.Single((await Get(f, a)).Friends).PlayerId);
        await Act(f, b, Request(CommunityAction.FriendRemove, player: a.Player.PlayerId));
        Assert.Empty((await Get(f, a)).Friends);
        await Act(f, b, new CommunityRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Action = CommunityAction.ProfileUpdate, Text = "Public bio", InvitationsOpen = false, Notifications = false });
        await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.FriendInvite, player: b.Player.PlayerId)));
        Assert.False((await Client(f).ProfileAsync(b.Player, Bearer(a.SessionToken))).InvitationsOpen);
    }

    [Fact]
    public async Task OnlyOwnerCanGrantTransferDisbandAndTransferRevokesOldInvitations()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby"); var c = await f.RegisterAndLogin("carol");
        long group = await Group(f, a, b);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, c, Request(CommunityAction.TreasuryDeposit, group, silver: 10)));
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.TreasuryPermission, group, b.Player.PlayerId)));
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.AllianceDisband, group)));
        await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.AllianceTransfer, group, c.Player.PlayerId)));
        await Act(f, a, Request(CommunityAction.AllianceInvite, group, c.Player.PlayerId));
        long invitation = Assert.Single((await Get(f, c)).Invitations).Id;
        await Fund(f, a.Player.PlayerId, 100);
        await Act(f, a, Request(CommunityAction.TreasuryDeposit, group, silver: 100));
        await Act(f, a, Request(CommunityAction.AllianceTransfer, group, b.Player.PlayerId));
        await Assert.ThrowsAsync<RpcException>(() => Act(f, c, Request(CommunityAction.InviteAccept, invite: invitation)));
        await Act(f, a, Request(CommunityAction.AllianceLeave, group));
        await Act(f, b, Request(CommunityAction.AllianceDisband, group));
        Assert.Equal(100, (await Clients(f, b.SessionToken).Wallet.GetAsync(new Empty(), Bearer(b.SessionToken))).Balance);
        Assert.Equal(0, (await Clients(f, a.SessionToken).Wallet.GetAsync(new Empty(), Bearer(a.SessionToken))).Balance);
    }

    [Fact]
    public async Task BlockingRevokesFriendshipAndTreasuryRightsButKeepsAllianceProtection()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long group = await Group(f, a, b);
        await Act(f, a, Request(CommunityAction.FriendInvite, player: b.Player.PlayerId));
        await Act(f, b, Request(CommunityAction.InviteAccept, invite: Assert.Single((await Get(f, b)).Invitations).Id));
        await Fund(f, a.Player.PlayerId, 100);
        await Act(f, a, Request(CommunityAction.TreasuryDeposit, group, silver: 100));
        var rights = Request(CommunityAction.TreasuryPermission, group, b.Player.PlayerId); rights.MayWithdraw = true; rights.DailyCap = 100;
        await Act(f, a, rights);
        await Act(f, a, Request(CommunityAction.AllianceChat, group, text: "hidden on block"));
        await SocialFixtures.Social(f, b.SessionToken).Social.BlockAsync(a.Player, Bearer(b.SessionToken));
        Assert.Empty((await Get(f, b)).Friends);
        Assert.Empty((await Get(f, b)).Chat);
        Assert.False((await Get(f, b)).Alliance.Members.Single(m => m.Player.PlayerId == b.Player.PlayerId).MayWithdraw);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.TreasuryWithdraw, group, silver: 1)));
        using var scope = f.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<AllianceService>().AreAllies(a.Player.PlayerId, b.Player.PlayerId));
    }

    [Theory]
    [InlineData(PawnMode.PawnSnapshot)]
    [InlineData(PawnMode.PawnReal)]
    public async Task AlliedRaidsAreRejectedBeforeEscrow(PawnMode mode)
    {
        using var f = RaidServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        await Group(f, a, b);
        await MakeTargetRaidable(f, b.Player.PlayerId);
        var api = RaidClientsOf(f, a.SessionToken);
        var ex = await Assert.ThrowsAsync<RpcException>(() => api.Raids.LaunchAsync(Launch(b.Player.PlayerId, mode: mode), api.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.AlliedTarget, ex.ErrorDetail()?.Code);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Empty(await db.Raids.ToListAsync());
        Assert.Equal(0, await db.Players.Where(p => p.Id == a.Player.PlayerId).Select(p => p.LastOutgoingTick).SingleAsync());
    }

    [Fact]
    public async Task AlliedLiveRaidRejectedButVisitAllowed()
    {
        using var f = LiveServer();
        var a = await Connected(f, "alice"); var b = await Connected(f, "bobby");
        await Group(f, a.Login, b.Login);
        await MakeTargetRaidable(f, b.PlayerId);
        var ex = await Assert.ThrowsAsync<RpcException>(() => a.Live.ProposeAsync(Propose(b.PlayerId, LiveKind.LiveRaid), a.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.AlliedTarget, ex.ErrorDetail()?.Code);
        var visit = await a.Live.ProposeAsync(Propose(b.PlayerId, LiveKind.LiveVisit), a.Auth);
        Assert.Equal(LiveKind.LiveVisit, visit.Kind);
    }

    [Theory]
    [InlineData("pirates")]
    [InlineData("toxic-fallout")]
    public async Task AlliedHarmfulIncidentsDoNotDebitWallet(string entry)
    {
        using var f = RaidServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        await Group(f, a, b); await MakeTargetRaidable(f, b.Player.PlayerId); await Fund(f, a.Player.PlayerId, 10000);
        var api = new Protocol.V1.Incidents.IncidentsClient(f.CreateChannel());
        var ex = await Assert.ThrowsAsync<RpcException>(() => api.OrderAsync(new OrderIncidentRequest { ClientRequestId = Guid.NewGuid().ToString("N"), TargetPlayerId = b.Player.PlayerId, EntryId = entry, Level = 1 }, Bearer(a.SessionToken)).ResponseAsync);
        Assert.Equal(ErrorCode.AlliedTarget, ex.ErrorDetail()?.Code);
        Assert.Equal(10000, (await Clients(f, a.SessionToken).Wallet.GetAsync(new Empty(), Bearer(a.SessionToken))).Balance);
    }

    [Fact]
    public async Task JoiningDuringQueuedHarmIsRefusedAndKeepsOriginalAction()
    {
        using var f = RaidServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        await MakeTargetRaidable(f, b.Player.PlayerId);
        long group = (await Act(f, a, Request(CommunityAction.AllianceCreate, text: "Pioneers"))).AllianceId;
        await Act(f, a, Request(CommunityAction.AllianceInvite, group, b.Player.PlayerId));
        long invite = Assert.Single((await Get(f, b)).Invitations).Id;
        var api = RaidClientsOf(f, a.SessionToken);
        await api.Raids.LaunchAsync(Launch(b.Player.PlayerId), api.Auth);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.InviteAccept, invite: invite)));
        Assert.Null((await Get(f, b)).Alliance);
        Assert.Single((await api.Raids.MineAsync(new Empty(), api.Auth)).Raids);
    }

    [Fact]
    public async Task PublicAndModerationProfilesHaveIdenticalSafeProjectionAndSelfRenameIsReused()
    {
        using var f = EconomyServer().WithSetting("Admin:ApiToken", "test-token");
        var a = await f.RegisterAndLogin("secretlogin", playerName: "PublicName");
        await Act(f, a, new CommunityRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Action = CommunityAction.ProfileUpdate, Text = "Bio", InvitationsOpen = true, Notifications = true });
        var rename = Request(CommunityAction.ProfileRename, text: "NewName");
        await Act(f, a, rename); await Act(f, a, rename);
        using var http = f.CreateClient();
        string publicJson = await http.GetStringAsync($"/api/public/profiles/{a.Player.PlayerId}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/profiles/{a.Player.PlayerId}");
        request.Headers.Authorization = new("Bearer", "test-token");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(publicJson, await response.Content.ReadAsStringAsync());
        var fields = JsonDocument.Parse(publicJson).RootElement;
        Assert.Equal(["playerId", "name", "bio", "lastSeen", "invitationsOpen", "notifications"], fields.EnumerateObject().Select(p => p.Name));
        Assert.Equal("NewName", fields.GetProperty("name").GetString());
        Assert.DoesNotContain("secretlogin", publicJson);
        using var scope = f.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().AdminActions.Where(a => a.Action == "rename").ToListAsync());
    }
}
