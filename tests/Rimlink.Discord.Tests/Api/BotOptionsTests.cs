using Rimlink.Discord.Options;
using Xunit;

namespace Rimlink.Discord.Tests.Api;

public class BotOptionsTests
{
    private static BotOptions Valid() => new()
    {
        Server = { ApiToken = "discord-bot-token-0123456789" },
        Discord = { Token = "discord-token", GuildId = 1 },
    };

    [Fact]
    public void DefaultsNeedTheTwoTokens()
    {
        var errors = BotOptions.Validate(new BotOptions());

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("Server:ApiToken"));
        Assert.Contains(errors, e => e.Contains("Discord:Token"));
    }

    [Fact]
    public void ValidOptionsHaveNoErrors() => Assert.Empty(BotOptions.Validate(Valid()));

    [Fact]
    public void BadLanguageUrlAndPinAreReported()
    {
        var o = Valid();
        o.Discord.Language = "de";
        o.Server.Url = "rimlink:25560";
        o.Server.CertificateSha256 = "abc";

        var errors = BotOptions.Validate(o);

        Assert.Contains(errors, e => e.Contains("Language"));
        Assert.Contains(errors, e => e.Contains("Server:Url"));
        Assert.Contains(errors, e => e.Contains("CertificateSha256"));
    }

    [Fact]
    public void A64HexPinIsValid()
    {
        var o = Valid();
        o.Server.CertificateSha256 = new string('A', 32) + new string('f', 32);

        Assert.Empty(BotOptions.Validate(o));
    }
}
