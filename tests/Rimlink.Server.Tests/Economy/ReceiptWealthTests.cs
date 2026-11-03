using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RecoveryFixtures;

namespace Rimlink.Server.Tests.Economy;

public sealed class ReceiptWealthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStoredReceiptExplainsPhysicalWealthBeforeTheDeliveryAck(bool withdraw)
    {
        using var fixture = EconomyFixtures.EconomyServer().WithSetting("Economy:ServerWealthPercent", "100")
            .WithSetting("Integrity:WealthWarnPerDay", "1").WithSetting("Integrity:WealthFreezePerDay", "2");
        await fixture.RegisterAndLogin("nova"); var login = await Login(fixture);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] before = Save(100); var firstHeader = Header(before, 100); firstHeader.WealthTotal = firstHeader.WealthItems = 1000;
        await SaveFixtures.Upload(saves, login.SessionToken, before, firstHeader);
        long parcelId;
        if (withdraw)
        {
            await EconomyFixtures.Fund(fixture, login.Player.PlayerId, 20);
            parcelId = (await EconomyFixtures.Clients(fixture, login.SessionToken).Wallet.WithdrawAsync(new WithdrawRequest
                { ClientRequestId = "withdraw", Silver = 20 }, Bearer(login.SessionToken))).Id;
        }
        else
        {
            using var scope = fixture.Services.CreateScope();
            parcelId = (await scope.ServiceProvider.GetRequiredService<MailboxService>().Create(login.Player.PlayerId, null, ParcelKindDb.Admin,
                new ItemList { Items = { EconomyFixtures.Item("Steel", 5, marketValueX100: 10000) } }, null, 20, null)).Id;
        }
        byte[] after = Save(200, parcels: [parcelId]); var secondHeader = Header(after, 200);
        secondHeader.WealthTotal = secondHeader.WealthItems = 1000 + (withdraw ? 20 : 500);

        await SaveFixtures.Upload(saves, login.SessionToken, after, secondHeader);

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Null((await db.Players.SingleAsync()).EconomyFrozenAt);
        Assert.Null((await db.Parcels.SingleAsync()).DeliveredAt);
        Assert.Equal(withdraw ? 0 : 20, await check.ServiceProvider.GetRequiredService<ServerWealth>().Of(login.Player.PlayerId));
    }
}
