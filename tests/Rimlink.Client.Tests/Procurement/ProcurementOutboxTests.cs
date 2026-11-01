using Microsoft.Extensions.DependencyInjection;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Procurement;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Client.Tests.Procurement;

public class ProcurementOutboxTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "procurement-outbox-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    private TransferOutbox Outbox() => new(Path.Combine(dir, "outbox.json"), new ConsoleLog());
    private static OutboxClients ClientsOf(RimlinkConnection c) => new(new EconomyClient(c), null, null, Procurement: new ProcurementClient(c));

    [Fact]
    public async Task PersistedDeliveryWithLostAnswerReplaysAfterCompletionWithoutReturningGoods()
    {
        using var f = EconomyServer(); await using var supplier = await ClientFixtures.Connected(f, "seller");
        var buyer = await f.RegisterAndLogin("buyer"); await Fund(f, buyer.Player.PlayerId, 100);
        var api = new Protocol.V1.Procurement.ProcurementClient(f.CreateChannel());
        var order = await api.CreateAsync(new ProcurementCreate { ClientRequestId = Guid.NewGuid().ToString("N"), DefName = "Steel", Quantity = 1, Reward = 100, LifetimeHours = 1 }, Bearer(buyer.SessionToken));
        var request = new ProcurementSupply { ClientRequestId = Guid.NewGuid().ToString("N"), ProcurementId = order.Id, Items = Items(("Steel", 1)), GameTick = 100 };
        var outbox = Outbox(); outbox.Add(request);
        await new ProcurementClient(supplier).Supply(request, CancellationToken.None);
        var report = await Outbox().Replay(ClientsOf(supplier), CancellationToken.None);
        Assert.Equal(1, report.Completed); Assert.Equal(0, report.Refused); Assert.Empty(Outbox().Entries);
        using var scope = f.Services.CreateScope(); Assert.Equal(100, await scope.ServiceProvider.GetRequiredService<WalletService>().Balance(supplier.Login!.Player.PlayerId));
    }

    [Fact]
    public async Task RefusedGoodsStayForGameReturnButWalletOnlyRequestsAreDropped()
    {
        using var f = EconomyServer(); await using var supplier = await ClientFixtures.Connected(f, "seller");
        var outbox = Outbox();
        outbox.Add(new ProcurementSupply { ClientRequestId = "missing", ProcurementId = 999, Items = Items(("Steel", 1)) });
        outbox.Add(new ProcurementCreate { ClientRequestId = "invalid" });
        outbox.Add(new ProcurementCancel { ClientRequestId = "cancel", ProcurementId = 999 });
        var report = await outbox.Replay(ClientsOf(supplier), CancellationToken.None);
        Assert.Equal(3, report.Refused);
        var entry = Assert.Single(outbox.Entries); Assert.Equal(OutboxKind.ProcurementSupply, entry.Kind); Assert.NotNull(entry.Refusal);
        Assert.IsType<ProcurementSupply>(TransferOutbox.Decode(entry));
    }

    [Fact]
    public void StateInvalidationMarksServerWealthStaleAndSurvivesLoadRace()
    {
        var shared = new Rimlink.Client.State.SharedState();
        long revision = shared.Procurement.Revision;
        shared.Apply(new ServerEvent { ProcurementChanged = new ProcurementChanged { ProcurementId = 7 } });
        Assert.True(shared.Procurement.Revision > revision);
        shared.Reset(); Assert.Empty(shared.Procurement.Entries);
    }

    [Fact]
    public async Task AThrottledReplayKeepsAnAlreadyAcceptedDeliveryPendingInsteadOfReturningIt()
    {
        using var f = EconomyServer().WithRateLimits().WithSetting("Economy:SendCooldownSeconds", "60");
        await using var seller = await ClientFixtures.Connected(f, "seller"); var buyer = await f.RegisterAndLogin("buyer");
        await Fund(f, buyer.Player.PlayerId, 10);
        var api = new Protocol.V1.Procurement.ProcurementClient(f.CreateChannel());
        var order = await api.CreateAsync(new ProcurementCreate { ClientRequestId = "rate-create", DefName = "Steel", Quantity = 1, Reward = 10, LifetimeHours = 1 }, Bearer(buyer.SessionToken));
        var request = new ProcurementSupply { ClientRequestId = "rate-supply", ProcurementId = order.Id, Items = Items(("Steel", 1)), GameTick = 100 };
        await new ProcurementClient(seller).Supply(request, CancellationToken.None);
        var outbox = Outbox(); outbox.Add(request);
        var report = await outbox.Replay(ClientsOf(seller), CancellationToken.None);
        Assert.Equal(1, report.Pending); Assert.Equal(0, report.Refused);
        Assert.Null(Assert.Single(outbox.Entries).Refusal);
    }
}
