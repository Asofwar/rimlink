using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Social;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SocialFixtures;

namespace Rimlink.Client.Tests.Social;

/// <summary>Trade moves whose answer never came are replayed with their key: applied once, never undone by a later refusal.</summary>
public class TradeOutboxTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-outbox-" + Guid.NewGuid().ToString("N"));

    private TransferOutbox Outbox() => new(Path.Combine(dir, "outbox.json"), new ConsoleLog());

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static OutboxClients ClientsOf(RimlinkConnection connection) =>
        new(new EconomyClient(connection), null, null, null, new TradesClient(connection));

    [Fact]
    public async Task OpenAnsweredLateIsReplayedOnce()
    {
        using var fixture = EconomyServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var request = OpenTrade(bob.Player.PlayerId, Items(("Steel", 5)));
        var outbox = Outbox();
        outbox.Add(request);

        var first = await outbox.Replay(ClientsOf(nova), CancellationToken.None);
        outbox.Add(request);
        var second = await outbox.Replay(ClientsOf(nova), CancellationToken.None);

        Assert.Equal(1, first.Completed);
        Assert.Equal(1, second.Completed);
        Assert.Empty(outbox.Entries);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Trades.CountAsync());
    }

    [Fact]
    public async Task ReplayedSetSideAfterTheExchangeCompletesTheEntry()
    {
        using var fixture = EconomyServer();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        var nova = await fixture.RegisterAndLogin("nova");
        var novaTrades = new Rimlink.Protocol.V1.Trades.TradesClient(fixture.CreateChannel());
        var trade = await novaTrades.OpenAsync(OpenTrade(bob.Login!.Player.PlayerId, Items(("Steel", 5))), Bearer(nova.SessionToken));
        var request = TradeSide(trade.Id, Items(("Plasteel", 3)), baseRevision: trade.Revision);
        var bobSide = await new TradesClient(bob).SetSide(request, CancellationToken.None); // applied, the answer "lost"
        var outbox = Outbox();
        outbox.Add(request);
        var done = await novaTrades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = bobSide.Revision }, Bearer(nova.SessionToken));
        Assert.Equal(TradeStatus.TradeCompleted, done.Status);

        var report = await outbox.Replay(ClientsOf(bob), CancellationToken.None);

        Assert.Equal(1, report.Completed);
        Assert.Equal(0, report.Refused);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task SetSideOnACancelledTradeIsRefused()
    {
        using var fixture = EconomyServer();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        var nova = await fixture.RegisterAndLogin("nova");
        var novaTrades = new Rimlink.Protocol.V1.Trades.TradesClient(fixture.CreateChannel());
        var trade = await novaTrades.OpenAsync(OpenTrade(bob.Login!.Player.PlayerId, Items(("Steel", 5))), Bearer(nova.SessionToken));
        await novaTrades.CancelAsync(new TradeRef { TradeId = trade.Id }, Bearer(nova.SessionToken));
        var outbox = Outbox();
        outbox.Add(TradeSide(trade.Id, Items(("Plasteel", 3))));

        var report = await outbox.Replay(ClientsOf(bob), CancellationToken.None);

        Assert.Equal(1, report.Refused);
        var entry = Assert.Single(outbox.Entries);
        Assert.Equal(OutboxKind.TradeSetSide, entry.Kind);
        Assert.NotNull(entry.Refusal);
    }
}
