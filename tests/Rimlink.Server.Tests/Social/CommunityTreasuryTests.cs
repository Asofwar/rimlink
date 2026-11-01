using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
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
    public async Task CreditOverflowDoesNotDestroyTreasuryOrWrapWallet()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long id = await Group(f, a, b); await Fund(f, a.Player.PlayerId, 100);
        await Act(f, a, Request(CommunityAction.TreasuryDeposit, id, silver: 100));
        await Fund(f, a.Player.PlayerId, long.MaxValue);
        Assert.Equal(ErrorCode.TreasuryLimit, (await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.TreasuryWithdraw, id, silver: 1)))).ErrorDetail()?.Code);
        Assert.Equal(ErrorCode.TreasuryLimit, (await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.AllianceDisband, id)))).ErrorDetail()?.Code);
        Assert.Equal(100, (await Get(f, a)).Alliance.Silver);
        Assert.Equal(long.MaxValue, (await Clients(f, a.SessionToken).Wallet.GetAsync(new Empty(), Bearer(a.SessionToken))).Balance);
    }

    [Fact]
    public async Task LeavingAndRejoiningCannotResetDailyWithdrawalCap()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long id = await Group(f, a, b); await Fund(f, a.Player.PlayerId, 500);
        await Act(f, a, Request(CommunityAction.TreasuryDeposit, id, silver: 500));
        var rights = Request(CommunityAction.TreasuryPermission, id, b.Player.PlayerId); rights.MayWithdraw = true; rights.DailyCap = 100;
        await Act(f, a, rights); await Act(f, b, Request(CommunityAction.TreasuryWithdraw, id, silver: 100));
        await Act(f, b, Request(CommunityAction.AllianceLeave, id));
        await Act(f, a, Request(CommunityAction.AllianceInvite, id, b.Player.PlayerId));
        await Act(f, b, Request(CommunityAction.InviteAccept, invite: Assert.Single((await Get(f, b)).Invitations).Id));
        rights.ClientRequestId = Guid.NewGuid().ToString("N"); await Act(f, a, rights);
        Assert.Equal(100, (await Get(f, b)).Alliance.Members.Single(m => m.Player.PlayerId == b.Player.PlayerId).WithdrawnToday);
        Assert.Equal(ErrorCode.TreasuryLimit, (await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.TreasuryWithdraw, id, silver: 1)))).ErrorDetail()?.Code);
    }

    [Fact]
    public async Task ConcurrentWithdrawalHasOneWinnerAndDoesNotOverdraw()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long id = await Group(f, a, b);
        await Fund(f, a.Player.PlayerId, 100);
        await Act(f, a, Request(CommunityAction.TreasuryDeposit, id, silver: 100));
        var permission = Request(CommunityAction.TreasuryPermission, id, b.Player.PlayerId); permission.MayWithdraw = true; permission.DailyCap = 100;
        await Act(f, a, permission);
        async Task<bool> Withdraw(LoginReply who)
        {
            try { await Act(f, who, Request(CommunityAction.TreasuryWithdraw, id, silver: 80)); return true; }
            catch (RpcException ex) when (ex.ErrorDetail()?.Code == ErrorCode.TreasuryLimit) { return false; }
        }
        var attempts = await Task.WhenAll(Withdraw(a), Withdraw(b));
        Assert.Single(attempts, x => x);
        Assert.Equal(20, (await Get(f, a)).Alliance.Silver);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(80, await db.Wallets.SumAsync(w => w.Balance));
        Assert.Single(await db.Set<AllianceAudit>().Where(x => x.Kind == "withdraw").ToListAsync());
    }

    [Theory]
    [InlineData("freeze", CommunityAction.TreasuryDeposit, ErrorCode.PlayerFrozen)]
    [InlineData("debt", CommunityAction.TreasuryDeposit, ErrorCode.InDebt)]
    [InlineData("threat", CommunityAction.TreasuryDeposit, ErrorCode.UnderThreat)]
    [InlineData("freeze", CommunityAction.TreasuryWithdraw, ErrorCode.PlayerFrozen)]
    [InlineData("debt", CommunityAction.TreasuryWithdraw, ErrorCode.InDebt)]
    [InlineData("threat", CommunityAction.TreasuryWithdraw, ErrorCode.UnderThreat)]
    public async Task TreasuryPreservesFreezeDebtAndThreatRules(string restriction, CommunityAction action, ErrorCode code)
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long id = await Group(f, a, b); await Fund(f, a.Player.PlayerId, 500);
        await Act(f, a, Request(CommunityAction.TreasuryDeposit, id, silver: 100));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            long restricted = action == CommunityAction.TreasuryDeposit ? a.Player.PlayerId : b.Player.PlayerId;
            if (restriction == "freeze") await db.Players.Where(p => p.Id == restricted).ExecuteUpdateAsync(s => s.SetProperty(p => p.EconomyFrozenAt, DateTime.UtcNow));
            if (restriction == "debt") await scope.ServiceProvider.GetRequiredService<WalletService>().Charge(restricted, 1000, "storage", 0);
            if (restriction == "threat") db.Raids.Add(new Rimlink.Server.Storage.Entities.Raid { AttackerPlayerId = 999, TargetPlayerId = restricted, State = RaidStateDb.Queued, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
        }
        var ex = await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(action, id, silver: 10)));
        Assert.Equal(code, ex.ErrorDetail()?.Code);
        Assert.Equal(100, (await Get(f, a)).Alliance.Silver);
    }

    [Fact]
    public async Task ReplayAfterFreezeReturnsOriginalReceiptAndChangedAmountIsRefused()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long id = await Group(f, a, b); await Fund(f, a.Player.PlayerId, 500);
        var deposit = Request(CommunityAction.TreasuryDeposit, id, silver: 100);
        var receipt = await Act(f, a, deposit);
        using (var scope = f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.Where(p => p.Id == a.Player.PlayerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.EconomyFrozenAt, DateTime.UtcNow));
        Assert.Equal(receipt, await Act(f, a, deposit));
        deposit.Silver = 200;
        Assert.Equal(ErrorCode.CommunityIntent, (await Assert.ThrowsAsync<RpcException>(() => Act(f, a, deposit))).ErrorDetail()?.Code);
        Assert.Equal(100, (await Get(f, a)).Alliance.Silver);
    }

    [Fact]
    public async Task RevokingPermissionDoesNotResetSpentDailyAllowance()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long id = await Group(f, a, b); await Fund(f, a.Player.PlayerId, 500);
        await Act(f, a, Request(CommunityAction.TreasuryDeposit, id, silver: 500));
        var rights = Request(CommunityAction.TreasuryPermission, id, b.Player.PlayerId); rights.MayWithdraw = true; rights.DailyCap = 100;
        await Act(f, a, rights);
        await Act(f, b, Request(CommunityAction.TreasuryWithdraw, id, silver: 80));
        rights = rights.Clone(); rights.ClientRequestId = Guid.NewGuid().ToString("N"); rights.MayWithdraw = false;
        await Act(f, a, rights);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.TreasuryWithdraw, id, silver: 1)));
        rights = rights.Clone(); rights.ClientRequestId = Guid.NewGuid().ToString("N"); rights.MayWithdraw = true;
        await Act(f, a, rights);
        await Assert.ThrowsAsync<RpcException>(() => Act(f, b, Request(CommunityAction.TreasuryWithdraw, id, silver: 21)));
        await Act(f, b, Request(CommunityAction.TreasuryWithdraw, id, silver: 20));
        Assert.Equal(400, (await Get(f, a)).Alliance.Silver);
    }

    [Fact]
    public async Task MemberLimitAndNewMemberChatHistoryAreEnforced()
    {
        using var f = EconomyServer();
        var a = await f.RegisterAndLogin("alice"); var b = await f.RegisterAndLogin("bobby");
        long id = (await Act(f, a, Request(CommunityAction.AllianceCreate, text: "Pioneers"))).AllianceId;
        await Act(f, a, Request(CommunityAction.AllianceChat, id, text: "before you joined"));
        await Act(f, a, Request(CommunityAction.AllianceInvite, id, b.Player.PlayerId));
        await Act(f, b, Request(CommunityAction.InviteAccept, invite: Assert.Single((await Get(f, b)).Invitations).Id));
        Assert.Empty((await Get(f, b)).Chat);
        await Act(f, b, Request(CommunityAction.AllianceChat, id, text: "member line"));
        Assert.Contains((await Get(f, a)).Chat, m => m.Text == "member line");
        await Act(f, b, Request(CommunityAction.AllianceLeave, id));
        Assert.DoesNotContain((await Get(f, a)).Chat, m => m.Text == "member line");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            for (int i = 0; i < 15; i++)
            {
                var account = new Account { Login = "test" + i, CreatedAt = DateTime.UtcNow };
                var player = new Rimlink.Server.Storage.Entities.Player { Name = "Test" + i, Account = account };
                db.Players.Add(player); await db.SaveChangesAsync();
                db.Set<AllianceMember>().Add(new AllianceMember { PlayerId = player.Id, AllianceId = id, JoinedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.CommunityLimit, (await Assert.ThrowsAsync<RpcException>(() => Act(f, a, Request(CommunityAction.AllianceInvite, id, b.Player.PlayerId)))).ErrorDetail()?.Code);
    }

    [Fact]
    public async Task TreasuryAndReplayReceiptSurviveServerRestart()
    {
        string dataDir;
        long playerId, group;
        var create = Request(CommunityAction.AllianceCreate, text: "Persistent");
        var deposit = Request(CommunityAction.TreasuryDeposit, silver: 120);
        using (var f = EconomyServer().KeepDataDir())
        {
            var a = await f.RegisterAndLogin("alice"); playerId = a.Player.PlayerId;
            group = (await Act(f, a, create)).AllianceId; deposit.AllianceId = group;
            await Fund(f, playerId, 500); await Act(f, a, deposit);
            dataDir = f.DataDir;
        }
        using var restarted = EconomyServer().WithDataDir(dataDir).WithoutWorld();
        var login = await new Protocol.V1.Auth.AuthClient(restarted.CreateChannel()).LoginAsync(PasswordLogin("alice", "pw"));
        Assert.Equal(group, (await Act(restarted, login, create)).AllianceId);
        await Act(restarted, login, deposit);
        Assert.Equal(120, (await Get(restarted, login)).Alliance.Silver);
        Assert.Equal(380, (await Clients(restarted, login.SessionToken).Wallet.GetAsync(new Empty(), Bearer(login.SessionToken))).Balance);
    }
}
