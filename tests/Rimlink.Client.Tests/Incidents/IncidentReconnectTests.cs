using Rimlink.Client.Economy;
using Rimlink.Client.Incidents;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.IncidentFixtures;

namespace Rimlink.Client.Tests.Incidents;

/// <summary>Core/server checks for the Pending/report gap; the vanilla worker is not executed here.</summary>
public class IncidentReconnectTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStalePendingReadAfterReconnectCannotRunAHandledIncidentAgain(bool fired)
    {
        using var fixture = IncidentServer();
        await using var buyer = await ClientFixtures.Connected(fixture, "nova");
        await Fund(fixture, buyer.Login!.Player.PlayerId, 10000);
        var buyersApi = new IncidentClient(buyer);
        var state = new IncidentsState();
        Incident order;
        IncidentList stale;
        await using (var target = await ClientFixtures.Connected(fixture, "bob"))
        {
            var api = new IncidentClient(target);
            order = await buyersApi.Order(Order(target.Login!.Player.PlayerId, "trader", 0), CancellationToken.None);
            await api.Announce(order.Id, 100, CancellationToken.None);
            stale = await api.Pending(CancellationToken.None);
            state.Load(stale, new IncidentList(), new IncidentCatalog());
            Assert.Equal(order.Id, Assert.Single(state.Due(100, new HashSet<long>())).Id);
            state.Remove(order.Id); // the game handles the worker before the report response arrives
        }
        await using var reconnect = ClientFixtures.Connect(fixture, "bob");
        await reconnect.ConnectAsync(CancellationToken.None);
        var targetsApi = new IncidentClient(reconnect);
        Assert.Equal(order.Id, Assert.Single((await targetsApi.Pending(CancellationToken.None)).Incidents).Id);

        state.Load(stale, new IncidentList(), new IncidentCatalog());
        Assert.Empty(state.Incoming);
        Assert.Empty(state.Due(100, new HashSet<long>()));
        string reason = fired ? "" : "no valid map location";
        await targetsApi.Report(order.Id, fired, reason, CancellationToken.None);
        await targetsApi.Report(order.Id, fired, reason, CancellationToken.None);

        Assert.Empty((await targetsApi.Pending(CancellationToken.None)).Incidents);
        var resolved = Assert.Single((await buyersApi.Mine(CancellationToken.None)).Incidents);
        Assert.Equal(fired ? IncidentState.IncidentFired : IncidentState.IncidentFailed, resolved.State);
        Assert.Equal(fired ? 10000 - order.Price : 10000, (await new EconomyClient(buyer).Wallet(CancellationToken.None)).Balance);
        Assert.Equal(reason, resolved.Reason);
    }
}
