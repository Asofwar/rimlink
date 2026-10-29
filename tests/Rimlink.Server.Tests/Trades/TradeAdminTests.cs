using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SocialFixtures;

namespace Rimlink.Server.Tests.Trades;

/// <summary>Trades closed from outside (a block, a reset, an admin) and the admin tools around them.</summary>
public class TradeAdminTests
{
    private const string ModToken = "mod-token-0123456789abcdefgh";

    private sealed record Party(long Id, SocialClients C, Metadata Auth);

    private static ServerFixture Server() => EconomyServer()
        .WithSetting("Auth:AdminLogins:0", "admin")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithToken(0, "mod", ModToken, "moderate");

    private static async Task<Party> Join(ServerFixture fixture, string login, string name)
    {
        var reply = await fixture.RegisterAndLogin(login, playerName: name);
        await Fund(fixture, reply.Player.PlayerId, 100);
        return new Party(reply.Player.PlayerId, SocialFixtures.Social(fixture, reply.SessionToken), Bearer(reply.SessionToken));
    }

    private static async Task<long> Balance(Party p) => (await p.C.Economy.Wallet.GetAsync(new Empty(), p.Auth)).Balance;

    private static async Task<List<Parcel>> Mailbox(Party p) => (await p.C.Economy.Mailbox.ListAsync(new Empty(), p.Auth)).Parcels.ToList();

    private static async Task<Trade> TwoSided(Party nova, Party bob)
    {
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10)), silver: 30), nova.Auth);
        return await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Plasteel", 5)), silver: 20, baseRevision: trade.Revision), bob.Auth);
    }

    private static async Task<Actor> Admin(ServerFixture fixture)
    {
        var admin = await fixture.RegisterAndLogin("admin");
        return new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin");
    }

    [Fact]
    public async Task BlockCancelsOpenTradesAndReturnsBothSides()
    {
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await TwoSided(nova, bob);
        var gift = await bob.C.Economy.Transfer.SendAsync(Send(nova.Id, Items(("Gold", 2))), bob.Auth);

        await nova.C.Social.BlockAsync(new PlayerRef { PlayerId = bob.Id }, nova.Auth);

        var closed = Assert.Single((await nova.C.Trades.ListAsync(new Empty(), nova.Auth)).Trades);
        Assert.Equal(TradeStatus.TradeCancelled, closed.Status);
        Assert.Equal("blocked", closed.CloseReason);
        var back = Assert.Single(await Mailbox(bob));
        Assert.Equal(ParcelKind.ParcelTradeReturn, back.Kind);
        Assert.Equal("Plasteel", back.Items.Items.Single().DefName);
        var waiting = await Mailbox(nova);
        Assert.Equal(2, waiting.Count);
        Assert.Contains(waiting, p => p.Kind == ParcelKind.ParcelTradeReturn && p.Items.Items.Single().DefName == "Steel");
        Assert.True(waiting.Single(p => p.Id == gift.Id).AwaitingDecision);
        Assert.Equal(100, await Balance(nova));
        Assert.Equal(100, await Balance(bob));
        await nova.C.Economy.Mailbox.AnswerAsync(new ParcelAnswer { ParcelId = gift.Id, Accept = false }, nova.Auth);
        Assert.Equal(2, (await Mailbox(bob)).Count);
        Assert.NotEqual(trade.Revision, 0);
    }

    [Fact]
    public async Task ResetCancelsTheirTrades()
    {
        using var fixture = Server();
        var actor = await Admin(fixture);
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        await TwoSided(nova, bob);

        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(actor, bob.Id);

        var closed = Assert.Single((await nova.C.Trades.ListAsync(new Empty(), nova.Auth)).Trades);
        Assert.Equal(TradeStatus.TradeCancelled, closed.Status);
        Assert.Equal("colony reset", closed.CloseReason);
        Assert.Equal(ParcelKind.ParcelTradeReturn, Assert.Single(await Mailbox(nova)).Kind);
        Assert.Equal(100, await Balance(nova));
        var again = await fixture.RegisterAndLogin("bob"); // the reset ended Bob's session
        var bobAgain = new Party(bob.Id, SocialFixtures.Social(fixture, again.SessionToken), Bearer(again.SessionToken));
        Assert.Equal(ParcelKind.ParcelTradeReturn, Assert.Single(await Mailbox(bobAgain)).Kind);
        Assert.Equal(100, await Balance(bobAgain));
    }

    [Fact]
    public async Task AdminListsAndCancelsTrades()
    {
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await TwoSided(nova, bob);
        using var probe = fixture.Probe();
        var mod = fixture.Rest(ModToken);
        var admin = fixture.Rest(AdminRestExtensions.ApiToken);

        var list = JsonDocument.Parse(await mod.GetStringAsync($"/api/admin/trades?playerId={nova.Id}")).RootElement;
        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal(trade.Id, row.GetProperty("id").GetInt64());
        Assert.Equal("Nova", row.GetProperty("initiatorName").GetString());
        Assert.Equal("Bob", row.GetProperty("partnerName").GetString());
        Assert.Equal("Open", row.GetProperty("status").GetString());
        Assert.Contains("Plasteel", row.GetProperty("partnerSide").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await mod.PostAsJsonAsync($"/api/admin/trades/{trade.Id}/cancel", new { reason = "dupe check" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/trades/{trade.Id}/cancel", new { reason = "dupe check" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/admin/trades/9999/cancel", new { reason = "x" })).StatusCode);

        var closed = Assert.Single((await nova.C.Trades.ListAsync(new Empty(), nova.Auth)).Trades);
        Assert.Equal(TradeStatus.TradeCancelled, closed.Status);
        Assert.Equal("dupe check", closed.CloseReason);
        var action = FeedProbe.Data(await probe.Next("admin.action"));
        Assert.Equal("trade-cancel", action.GetProperty("action").GetString());
        Assert.DoesNotContain(await probe.Drain(TimeSpan.FromMilliseconds(300)), e => e.Type != "admin.action" && e.Type.Contains("trade"));
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().AdminActions.CountAsync(a => a.Action == "trade-cancel"));
    }

    [Fact]
    public async Task CardShowsBlocks()
    {
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        await nova.C.Social.BlockAsync(new PlayerRef { PlayerId = bob.Id }, nova.Auth);

        var card = JsonDocument.Parse(await fixture.Rest(ModToken).GetStringAsync($"/api/admin/players/{nova.Id}")).RootElement;

        Assert.Equal(["Bob"], card.GetProperty("blocks").EnumerateArray().Select(b => b.GetString()));
    }

    [Fact]
    public async Task ChatCommandsListAndCancelTrades()
    {
        using var fixture = Server();
        var actor = await Admin(fixture);
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await TwoSided(nova, bob);
        using var scope = fixture.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<AdminCommandRunner>();

        string listed = await runner.Execute(actor, ChatCommandParser.Parse("/trades Nova")!);
        string cancelled = await runner.Execute(actor, ChatCommandParser.Parse($"/trade cancel {trade.Id} too slow")!);
        string again = await runner.Execute(actor, ChatCommandParser.Parse($"/trade cancel {trade.Id}")!);

        Assert.Contains($"#{trade.Id}", listed);
        Assert.Contains("Bob", listed);
        Assert.Equal("trade cancelled", cancelled);
        Assert.Equal("no such open trade", again);
        Assert.Equal("too slow", Assert.Single((await nova.C.Trades.ListAsync(new Empty(), nova.Auth)).Trades).CloseReason);
    }
}
