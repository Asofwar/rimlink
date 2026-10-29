using Rimlink.Discord.Api;
using Rimlink.Discord.Commands;
using Rimlink.Discord.Tests.Support;
using Rimlink.Discord.Text;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Discord.Tests.Commands;

public class CommandHandlersTests
{
    private const string ModChatToken = "modchat-token-0123456789abc";
    private const string ReadChatToken = "readchat-token-0123456789ab";

    private static readonly Strings S = new("en");
    private static readonly Caller Everyone = new(1, "Joe", Level.Everyone);
    private static readonly Caller Mod = new(7, "Mod", Level.Moderator);
    private static readonly Caller Admin = new(9, "Boss", Level.Admin);

    private static (CommandHandlers Handlers, Confirmations Confirmations) Handlers(RimlinkApi api)
    {
        var confirmations = new Confirmations(TimeProvider.System, S);
        return (new CommandHandlers(api, new PlayerResolver(api, S), confirmations, S), confirmations);
    }

    private static BotFixture Bot() => new(null, ("modchat", ModChatToken, ["moderate", "chat"]), ("readchat", ReadChatToken, ["read", "chat"]));

    [Fact]
    public async Task PublicCommandsWorkForEveryone()
    {
        using var bot = Bot();
        var nova = await bot.Fixture.RegisterAndLogin("nova", playerName: "Nova");
        await new Protocol.V1.World.WorldClient(bot.Fixture.CreateChannel()).SettleAsync(new SettleRequest { Tile = 100, Name = "Nova Home" }, Bearer(nova.SessionToken));
        var n = Clients(bot.Fixture, nova.SessionToken);
        await n.Market.PostAsync(new PostRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Items = Items(("Steel", 50)), Price = 300, GameTick = 100 }, n.Auth);
        var (h, _) = Handlers(bot.Api());

        var online = await h.Online(Everyone);
        var status = await h.Status(Everyone);
        var market = await h.Market(Everyone, null);
        var player = await h.Player(Everyone, "nova");

        Assert.Contains("Nova", online.Text);
        Assert.Contains("1/50", status.Text);
        Assert.Contains("Steel ×50", market.Text);
        Assert.Contains("Nova Home", player.Text);
        Assert.All(new[] { online, status, market, player }, r => Assert.False(r.Ephemeral));
    }

    [Fact]
    public async Task RefusedCommandsMakeNoRequest()
    {
        using var bot = Bot();
        var spy = new CountingHandler();
        var (h, _) = Handlers(bot.Api(spy: spy));

        var replies = new[] { await h.Kick(Everyone, "nova", null), await h.Ban(Mod, "nova", null, null), await h.Reset(Mod, "nova"), await h.Shutdown(Mod, 60, null) };

        Assert.All(replies, r => Assert.Equal(S.T("cmd.denied"), r.Text));
        Assert.All(replies, r => Assert.True(r.Ephemeral));
        Assert.Equal(0, spy.Count);
    }

    [Fact]
    public async Task KickRecordsTheDiscordActor()
    {
        using var bot = Bot();
        await bot.Fixture.RegisterAndLogin("nova", playerName: "Nova");
        var api = bot.Api();
        var (h, _) = Handlers(api);

        var reply = await h.Kick(Mod, "nova", "spam");

        Assert.Contains("Nova", reply.Text);
        Assert.True(reply.Ephemeral);
        Assert.Equal("api:discord-bot:discord:Mod (7)", (await api.Actions(1)).Single().ActorName);
    }

    [Fact]
    public async Task IrreversibleCommandsWaitForConfirmation()
    {
        using var bot = Bot();
        await bot.Fixture.RegisterAndLogin("nova", playerName: "Nova");
        var api = bot.Api();
        var (h, confirmations) = Handlers(api);

        var reply = await h.Reset(Admin, "nova");

        Assert.NotNull(reply.Confirm);
        Assert.True(reply.Ephemeral);
        Assert.Contains("Nova", reply.Confirm!.Description);
        Assert.DoesNotContain(await api.Actions(10), a => a.Action == "reset");

        var done = await confirmations.Press(reply.Confirm.Id, Admin.UserId, Level.Admin, confirm: true);
        Assert.Equal(S.T("cmd.done"), done.Text);
        Assert.Contains(await api.Actions(10), a => a.Action == "reset");
    }

    [Fact]
    public async Task NotFoundBecomesItsOwnText()
    {
        using var bot = Bot();
        var (h, _) = Handlers(bot.Api());

        Assert.Equal(S.T("cmd.notfound"), (await h.Pardon(Admin, 9999)).Text);
    }

    [Fact]
    public async Task NoAnswerBecomesItsOwnText()
    {
        var (h, _) = Handlers(new RimlinkApi(new HttpClient(new DownHandler()) { BaseAddress = new Uri("https://rimlink:25560/api/admin/") }));

        var reply = await h.Status(Everyone);

        Assert.Equal(S.T("cmd.noanswer"), reply.Text);
        Assert.True(reply.Ephemeral);
    }

    [Fact]
    public async Task UnknownPlayerIsItsOwnText()
    {
        using var bot = Bot();
        var (h, _) = Handlers(bot.Api());

        Assert.Equal(S.T("cmd.player.unknown", "ghost"), (await h.Kick(Mod, "ghost", null)).Text);
    }

    [Fact]
    public async Task MissingScopeNamesTheScope()
    {
        using var bot = Bot();
        await bot.Fixture.RegisterAndLogin("nova", playerName: "Nova");
        var (modOnly, _) = Handlers(bot.Api(ModChatToken));
        var (readOnly, _) = Handlers(bot.Api(ReadChatToken));

        Assert.Equal(S.T("cmd.forbidden", "admin"), (await modOnly.Ban(Admin, "nova", null, "x")).Text);
        Assert.Equal(S.T("cmd.forbidden", "moderate"), (await readOnly.Kick(Mod, "nova", null)).Text);
        Assert.Contains("Nova", (await readOnly.Online(Everyone)).Text);
        Assert.Contains("/50", (await readOnly.Status(Everyone)).Text);
    }

    [Fact]
    public async Task AdminCommandsReachTheServer()
    {
        using var bot = Bot();
        var nova = await bot.Fixture.RegisterAndLogin("nova", playerName: "Nova");
        var api = bot.Api();
        var (h, _) = Handlers(api);

        Assert.Contains("500", (await h.Wallet(Admin, "nova", 500, "gift")).Text);
        Assert.Equal(S.T("cmd.done"), (await h.EconomyFreeze(Admin, true, "audit")).Text);
        Assert.True((await api.Status()).Economy!.Frozen);
        var ban = await h.Ban(Admin, "nova", 1, "griefing");
        Assert.Contains("#", ban.Text);
        Assert.Contains("griefing", (await h.Bans(Mod)).Text);
    }
}
