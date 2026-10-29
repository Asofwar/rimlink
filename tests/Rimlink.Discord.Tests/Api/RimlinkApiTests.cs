using Rimlink.Discord.Api;
using Rimlink.Discord.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Discord.Tests.Api;

public class RimlinkApiTests
{
    [Fact]
    public async Task SendsTokenAndActor()
    {
        using var bot = new BotFixture();
        var nova = await bot.Fixture.RegisterAndLogin("nova", playerName: "Nova");
        var api = bot.Api();

        await api.Kick("discord:Mod (1)", nova.Player.PlayerId, "test");

        Assert.Equal("api:discord-bot:discord:Mod (1)", (await api.Actions(1)).Single().ActorName);
    }

    [Fact]
    public async Task ErrorsCarryStatusAndDetail()
    {
        using var bot = new BotFixture();

        var ex = await Assert.ThrowsAsync<RimlinkApiException>(() => bot.Api().Kick("discord:Mod (1)", 999, "x"));

        Assert.Equal(404, ex.Status);
        Assert.Contains("999", ex.Detail);
    }

    [Fact]
    public async Task WrongTokenIs401()
    {
        using var bot = new BotFixture();

        Assert.Equal(401, (await Assert.ThrowsAsync<RimlinkApiException>(() => bot.Api("wrong-token-0123456789abcdef").Status())).Status);
    }

    [Fact]
    public async Task UnreachableServerIsStatusZero()
    {
        var api = new RimlinkApi(new HttpClient(new DownHandler()) { BaseAddress = new Uri("https://rimlink:25560/api/admin/") });

        Assert.Equal(0, (await Assert.ThrowsAsync<RimlinkApiException>(() => api.Status())).Status);
    }

    [Fact]
    public async Task ReadsDeserialize()
    {
        using var bot = new BotFixture();
        var nova = await bot.Fixture.RegisterAndLogin("nova", playerName: "Nova");
        await new Protocol.V1.World.WorldClient(bot.Fixture.CreateChannel()).SettleAsync(new SettleRequest { Tile = 100, Name = "Nova Home" }, Bearer(nova.SessionToken));
        var n = Clients(bot.Fixture, nova.SessionToken);
        await n.Market.PostAsync(new PostRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Items = Items(("Steel", 50)), Price = 300, GameTick = 100 }, n.Auth);
        var api = bot.Api();

        var status = await api.Status();
        Assert.Equal(1, status.PlayersOnline);
        Assert.Equal(50, status.MaxPlayers);
        Assert.False(status.Economy!.Frozen);

        var page = await api.Players("no");
        var item = page.Items.Single();
        Assert.Equal("Nova", item.Name);
        Assert.True(item.Online);

        var card = await api.Player(item.Id);
        Assert.Equal("Nova Home", card.Settlements.Single().Name);
        Assert.False(card.Peaceful);

        var lot = (await api.Lots(null)).Single();
        Assert.Equal("Nova", lot.SellerName);
        Assert.Equal("Steel ×50", lot.Summary);
        Assert.Equal(300, lot.Price);
    }

    [Fact]
    public async Task PostChatReturnsTheId()
    {
        using var bot = new BotFixture();

        var posted = await bot.Api().PostChat("Mod", "hello");

        Assert.True(posted.Id > 0);
    }
}
