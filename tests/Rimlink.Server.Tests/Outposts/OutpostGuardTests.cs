using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostGuardTests
{
    [Theory]
    [InlineData("withdraw")]
    [InlineData("send")]
    [InlineData("trade")]
    [InlineData("purchase")]
    [InlineData("auction-bid")]
    [InlineData("procurement")]
    [InlineData("incident")]
    [InlineData("alliance-deposit")]
    [InlineData("admin")]
    public async Task Prepared_reserve_is_unavailable_to_every_wallet_debit(string kind)
    {
        using var h = await Reserved();
        var wallet = Wallet(h);
        var error = await Assert.ThrowsAsync<RpcException>(() => wallet.Apply(1, -201, kind, 0));
        Assert.Equal(ErrorCode.WalletInsufficient, error.ErrorDetail()?.Code);
        Assert.Equal(700, await wallet.Balance(1));
        Assert.Empty(await wallet.History(1));
        Assert.Equal(200, await wallet.Available(1));
    }

    [Fact]
    public async Task Storage_fee_cannot_consume_prepared_reserve_but_unreserved_fee_may_create_debt()
    {
        using var h = await Reserved(); var wallet = Wallet(h);
        var error = await Assert.ThrowsAsync<RpcException>(() => wallet.Charge(1, 201, "storage", 0));
        Assert.Equal(ErrorCode.WalletInsufficient, error.ErrorDetail()?.Code);
        Assert.Equal(700, await wallet.Balance(1));
        await h.H.Db.OutpostOperations.ExecuteUpdateAsync(s => s.SetProperty(o => o.ReservedSilver, 0));
        await wallet.Charge(1, 701, "storage", 0);
        Assert.Equal(-1, await wallet.Balance(1));
    }

    [Fact]
    public async Task Reserve_is_not_a_debit_and_is_consumed_once_in_owning_transaction()
    {
        using var h = await Reserved(); var wallet = Wallet(h);
        Assert.Equal(700, await wallet.Balance(1)); Assert.Equal(200, await wallet.Available(1));
        await using var transaction = await h.H.Db.Database.BeginTransactionAsync();
        await h.H.Db.OutpostOperations.ExecuteUpdateAsync(s => s.SetProperty(o => o.ReservedSilver, 0));
        await wallet.Apply(1, -500, "outpost-found", 0);
        await transaction.CommitAsync();
        Assert.Equal(200, await wallet.Balance(1)); Assert.Equal(200, await wallet.Available(1));
        Assert.Equal(-500, Assert.Single(await wallet.History(1)).Delta);
    }

    [Fact]
    public async Task Credit_does_not_charge_or_release_a_prepared_reserve()
    {
        using var h = await Reserved(); var wallet = Wallet(h);
        await wallet.Apply(1, 50, "deposit", 0);
        Assert.Equal(750, await wallet.Balance(1)); Assert.Equal(250, await wallet.Available(1));
        Assert.Equal(500, await h.H.Db.OutpostOperations.SumAsync(o => o.ReservedSilver));
    }

    private static WalletService Wallet(SnapshotHarness h) => new(h.H.Db,
        new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance));

    private static async Task<SnapshotHarness> Reserved()
    {
        var h = await SnapshotHarness.Create();
        h.H.Db.Wallets.Add(new Rimlink.Server.Storage.Entities.Wallet { PlayerId = 1, Balance = 700 });
        h.H.Db.OutpostOperations.Add(new OutpostOperation { Id = Guid.NewGuid().ToString("D"), WorldId = "world-1",
            PlayerId = 1, RequestId = Guid.NewGuid().ToString("D"), Kind = "Found", State = "Prepared", ReservedSilver = 500 });
        await h.H.Db.SaveChangesAsync();
        return h;
    }
}
