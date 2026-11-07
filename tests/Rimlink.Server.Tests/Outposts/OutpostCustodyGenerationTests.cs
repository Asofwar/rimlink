using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostCustodyBridgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_escrow_cannot_transplant_custody_after_owner_generation_changes(bool sell)
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1);
        h.Db.Wallets.Add(new Rimlink.Server.Storage.Entities.Wallet { PlayerId = 2, Balance = 1000 }); await h.Db.SaveChangesAsync();
        var evidence = await h.Depart(); using var host = await h.StartHostAsync(); long lotId;
        using (var scope = host.Services.CreateScope())
            lotId = (await scope.ServiceProvider.GetRequiredService<MarketService>().Post(1,
                new PostRequest { Pawn = h.Blob.Clone(), Price = 150, ClientRequestId = evidence.ClientRequestId, GameTick = 1000 })).Id;
        long[] originalLinks = await h.Db.OutpostParcelLinks.AsNoTracking().OrderBy(p => p.ParcelId).Select(p => p.ParcelId).ToArrayAsync();
        // A generation boundary is not an instruction to move old escrow to the new colony.
        await h.Db.Players.Where(p => p.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveGeneration, 1));
        using (var scope = host.Services.CreateScope())
        {
            var market = scope.ServiceProvider.GetRequiredService<MarketService>();
            if (sell) await Assert.ThrowsAsync<RpcException>(() => market.Buy(2, lotId));
            else await Assert.ThrowsAsync<RpcException>(() => market.Withdraw(1, lotId));
        }
        var row = await h.Row(); Assert.Equal(0, row.SaveGeneration); Assert.Equal(1, row.PlayerId); Assert.Equal("Escrow", row.LocationKind);
        Assert.Equal(LotStatusDb.Open, (await h.Db.Lots.AsNoTracking().SingleAsync(l => l.Id == lotId)).Status);
        Assert.Equal(originalLinks, await h.Db.OutpostParcelLinks.AsNoTracking().OrderBy(p => p.ParcelId).Select(p => p.ParcelId).ToArrayAsync());
        Assert.Equal(1000, (await h.Db.Wallets.AsNoTracking().SingleAsync(w => w.PlayerId == 2)).Balance);
    }
}
