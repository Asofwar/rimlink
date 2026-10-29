using Rimlink.Discord.Gateway.Modules;
using Rimlink.Discord.Tests.Support;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Discord.Tests.Commands;

public class PlayerAutocompleteTests
{
    [Fact]
    public async Task LoginsAndSteamIdsAreNotSuggested()
    {
        using var bot = new BotFixture();
        var robert = await bot.Fixture.RegisterAndLogin("bob123", playerName: "Robert");
        var api = bot.Api();

        // The bot's admin token searches logins, SteamIDs and #ids too; any member may type into autocomplete.
        Assert.Empty(await PlayerAutocomplete.Suggestions(api, "bob123", CancellationToken.None));
        Assert.Empty(await PlayerAutocomplete.Suggestions(api, $"#{robert.Player.PlayerId}", CancellationToken.None));
        Assert.Equal(["Robert"], await PlayerAutocomplete.Suggestions(api, "rob", CancellationToken.None));
    }
}
