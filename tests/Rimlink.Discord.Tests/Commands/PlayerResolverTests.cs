using Rimlink.Discord.Commands;
using Rimlink.Discord.Tests.Support;
using Rimlink.Discord.Text;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Discord.Tests.Commands;

public class PlayerResolverTests : IAsyncLifetime
{
    private readonly BotFixture bot = new();
    private PlayerResolver resolver = null!;

    public async Task InitializeAsync()
    {
        foreach (var name in new[] { "Nova", "Novak", "Novella", "Bob" }) await bot.Fixture.RegisterAndLogin(name.ToLowerInvariant(), playerName: name);
        resolver = new PlayerResolver(bot.Api(), new Strings("en"));
    }

    public Task DisposeAsync()
    {
        bot.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExactWinsOverPrefix() => Assert.Equal("Nova", (await resolver.Resolve("nova", CancellationToken.None)).Player?.Name);

    [Fact]
    public async Task SinglePrefixResolves() => Assert.Equal("Bob", (await resolver.Resolve("bo", CancellationToken.None)).Player?.Name);

    [Fact]
    public async Task AmbiguousListsCandidates()
    {
        var (player, error) = await resolver.Resolve("novel", CancellationToken.None);
        Assert.Equal("Novella", player?.Name);

        (player, error) = await resolver.Resolve("no", CancellationToken.None);
        Assert.Null(player);
        foreach (var name in new[] { "Nova", "Novak", "Novella" }) Assert.Contains(name, error);
    }

    [Fact]
    public async Task APrefixOnATruncatedPageIsAmbiguous()
    {
        // 300 players match "jo"; the first page holds one name starting with it, the next pages may hold more.
        var api = new Rimlink.Discord.Api.RimlinkApi(new HttpClient(new StubHandler(
            """{"total":300,"items":[{"id":1,"name":"John","factionName":"","online":true},{"id":2,"name":"Ajo","factionName":"","online":true}]}"""))
            { BaseAddress = new Uri("https://rimlink:25560/api/admin/") });

        var (player, error) = await new PlayerResolver(api, new Strings("en")).Resolve("jo", CancellationToken.None);

        Assert.Null(player);
        Assert.Contains("John", error);
    }

    [Fact]
    public async Task UnknownIsAnError()
    {
        var (player, error) = await resolver.Resolve("zz", CancellationToken.None);

        Assert.Null(player);
        Assert.Equal(new Strings("en").T("cmd.player.unknown", "zz"), error);
    }
}
