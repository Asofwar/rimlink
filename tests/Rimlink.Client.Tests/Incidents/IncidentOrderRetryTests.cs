using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Incidents;
using Rimlink.Client.Net;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.IncidentFixtures;

namespace Rimlink.Client.Tests.Incidents;

public class IncidentOrderRetryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-incident-retry-" + Guid.NewGuid().ToString("N"));
    private TransferOutbox Outbox() => new(Path.Combine(dir, "outbox.json"), new ConsoleLog());
    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ChangedSelectionCannotReplaceTheUnansweredIntentEvenAfterReload()
    {
        var intent = Order(12, "pirates", 3, pods: true);
        var retry = new IncidentOrderRetry(Outbox());
        var first = retry.Begin(intent);
        var original = Assert.IsType<OrderIncidentRequest>(TransferOutbox.Decode(first));
        intent.TargetPlayerId = 34;
        intent.EntryId = "trader";
        intent.Level = 0;
        intent.Pods = false;

        var reloaded = Outbox();
        var again = new IncidentOrderRetry(reloaded).Begin(intent);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(original, TransferOutbox.Decode(again));
        Assert.Equal((12L, "pirates", 3, true), (original.TargetPlayerId, original.EntryId, original.Level, original.Pods));
        Assert.Single(reloaded.Entries);
    }

    [Fact]
    public void AResolvedOrderAllowsANewIntentWithANewKey()
    {
        var outbox = Outbox();
        var retry = new IncidentOrderRetry(outbox);
        var first = retry.Begin(Order(12));
        outbox.Remove(first.Id);
        var second = retry.Begin(Order(34, "trader", 0));

        Assert.NotEqual(first.Id, second.Id);
        var request = Assert.IsType<OrderIncidentRequest>(TransferOutbox.Decode(second));
        Assert.NotEqual(((OrderIncidentRequest)TransferOutbox.Decode(first)).ClientRequestId, request.ClientRequestId);
        Assert.Equal((34L, "trader", 0), (request.TargetPlayerId, request.EntryId, request.Level));
        Assert.Equal(second.Id, Assert.Single(Outbox().Entries).Id);
    }

    [Fact]
    public async Task LostReplyThenChangedSelectionReplaysOriginalAndChargesOnlyOnce()
    {
        using var fixture = IncidentServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var cara = await fixture.RegisterAndLogin("cara");
        await Fund(fixture, nova.Login!.Player.PlayerId, 10000);
        var api = new IncidentClient(nova);
        var outbox = Outbox();
        var first = new IncidentOrderRetry(outbox).Begin(Order(bob.Player.PlayerId, "trader", 0));
        var accepted = await api.Order((OrderIncidentRequest)TransferOutbox.Decode(first), CancellationToken.None);
        // The server committed, but the UI did not see the reply and kept the persisted entry.
        var reloaded = Outbox();
        var again = new IncidentOrderRetry(reloaded).Begin(Order(cara.Player.PlayerId, "resource-pod", 0));
        var replayed = await api.Order((OrderIncidentRequest)TransferOutbox.Decode(again), CancellationToken.None);

        Assert.Equal(accepted.Id, replayed.Id);
        Assert.Equal((bob.Player.PlayerId, "trader"), (replayed.Target.PlayerId, replayed.EntryId));
        Assert.Equal(new ReplayReport(1, 0, 0), await reloaded.Replay(new OutboxClients(new EconomyClient(nova), null, null, Incidents: api), CancellationToken.None));
        Assert.Empty(Outbox().Entries);

        var next = new IncidentOrderRetry(reloaded).Begin(Order(cara.Player.PlayerId, "resource-pod", 0));
        var nextOrder = await api.Order((OrderIncidentRequest)TransferOutbox.Decode(next), CancellationToken.None);
        Assert.NotEqual(accepted.Id, nextOrder.Id);
        Assert.Equal((cara.Player.PlayerId, "resource-pod"), (nextOrder.Target.PlayerId, nextOrder.EntryId));
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(2, await db.IncidentOrders.CountAsync());
        Assert.Equal(10000 - accepted.Price - nextOrder.Price, (await db.Wallets.SingleAsync(w => w.PlayerId == nova.Login.Player.PlayerId)).Balance);
        Assert.Equal(accepted.Price + nextOrder.Price, (await db.Wallets.SingleAsync(w => w.PlayerId == 0)).Balance);
    }

    [Fact]
    public async Task LegacyReusedKeyReturnsTheOldTargetAndTypeDespiteChangedSelection()
    {
        using var fixture = IncidentServer();
        var login = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var cara = await fixture.RegisterAndLogin("cara");
        await Fund(fixture, login.Player.PlayerId, 10000);
        var handler = new DropOrderReply(fixture.Server.CreateHandler());
        await using var nova = new RimlinkConnection(new ServerEndpoint("test", 1), new PasswordCredentials("nova", "pw"),
            new NoPrompt(), new ConsoleLog(), new ConnectionTransport(fixture.Server.BaseAddress, handler));
        await nova.ConnectAsync(CancellationToken.None);
        var api = new IncidentClient(nova);
        var original = Order(bob.Player.PlayerId, "trader", 0);
        await Assert.ThrowsAsync<RpcException>(() => api.Order(original, CancellationToken.None));
        Assert.True(handler.Dropped);
        var accepted = Assert.Single((await api.Mine(CancellationToken.None)).Incidents);
        // EventsTab used to retain only this key after a timeout, rebuilding every other field from the selection.
        var changed = Order(cara.Player.PlayerId, "resource-pod", 0, requestId: original.ClientRequestId);

        var replayed = await api.Order(changed, CancellationToken.None);

        Assert.Equal(accepted.Id, replayed.Id);
        Assert.NotEqual(changed.TargetPlayerId, replayed.Target.PlayerId);
        Assert.NotEqual(changed.EntryId, replayed.EntryId);
        Assert.Equal((bob.Player.PlayerId, "trader"), (replayed.Target.PlayerId, replayed.EntryId));
        Assert.Single((await api.Mine(CancellationToken.None)).Incidents);
    }

    private sealed class DropOrderReply(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Dropped { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (!Dropped && request.RequestUri!.AbsolutePath.EndsWith("/Order", StringComparison.Ordinal))
            {
                await response.Content.LoadIntoBufferAsync(ct);
                response.Dispose();
                Dropped = true;
                throw new HttpRequestException("test: order committed but its response was lost");
            }
            return response;
        }
    }

    [Fact]
    public async Task TransportFailureAfterCommitKeepsTheOriginalRequestOnDisk()
    {
        using var fixture = IncidentServer();
        var login = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await Fund(fixture, login.Player.PlayerId, 10000);
        var handler = new DropOrderReply(fixture.Server.CreateHandler());
        await using var connection = new RimlinkConnection(new ServerEndpoint("test", 1), new PasswordCredentials("nova", "pw"),
            new NoPrompt(), new ConsoleLog(), new ConnectionTransport(fixture.Server.BaseAddress, handler));
        await connection.ConnectAsync(CancellationToken.None);
        var outbox = Outbox();
        var entry = new IncidentOrderRetry(outbox).Begin(Order(bob.Player.PlayerId, "trader", 0));
        var api = new IncidentClient(connection);

        await Assert.ThrowsAsync<RpcException>(() => api.Order((OrderIncidentRequest)TransferOutbox.Decode(entry), CancellationToken.None));
        Assert.True(handler.Dropped);
        var committed = Assert.Single((await api.Mine(CancellationToken.None)).Incidents);
        var persisted = Outbox();
        var retry = new IncidentOrderRetry(persisted).Begin(Order(9999, "pirates", 4, pods: true));
        Assert.Equal(entry.Payload, retry.Payload);

        var report = await persisted.Replay(new OutboxClients(new EconomyClient(connection), null, null, Incidents: api), CancellationToken.None);

        Assert.Equal(new ReplayReport(1, 0, 0), report);
        Assert.Empty(Outbox().Entries);
        Assert.Equal(committed.Id, Assert.Single((await api.Mine(CancellationToken.None)).Incidents).Id);
        Assert.Equal(10000 - committed.Price, (await new EconomyClient(connection).Wallet(CancellationToken.None)).Balance);
    }

    [Fact]
    public async Task RefusedOrderDropsItsEntryWithoutReturningAnyGoods()
    {
        using var fixture = IncidentServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var outbox = Outbox();
        new IncidentOrderRetry(outbox).Begin(Order(nova.Login!.Player.PlayerId, "trader", 0));

        var result = await outbox.Replay(new OutboxClients(new EconomyClient(nova), null, null, Incidents: new IncidentClient(nova)), CancellationToken.None);

        Assert.Equal(new ReplayReport(0, 1, 0), result);
        Assert.Empty(Outbox().Entries);
    }

    [Fact]
    public async Task ReplayWithoutAnIncidentClientLeavesTheIntentWaiting()
    {
        using var fixture = IncidentServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var outbox = Outbox();
        var entry = new IncidentOrderRetry(outbox).Begin(Order(12));

        Assert.Equal(new ReplayReport(0, 0, 1), await outbox.Replay(new EconomyClient(nova), CancellationToken.None));
        Assert.Equal(entry.Id, Assert.Single(Outbox().Entries).Id);
    }
}
