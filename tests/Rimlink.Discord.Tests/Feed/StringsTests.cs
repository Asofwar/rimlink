using Rimlink.Discord.Text;
using Xunit;

namespace Rimlink.Discord.Tests.Feed;

public class StringsTests
{
    [Fact]
    public void BothLanguagesHaveTheSameKeys()
    {
        var en = new Strings("en").Keys.Order().ToList();
        var ru = new Strings("ru").Keys.Order().ToList();

        Assert.NotEmpty(en);
        Assert.Equal(en, ru);
    }

    [Fact]
    public void ArgumentsAreFormatted() =>
        Assert.Equal("**Nova** joined the server", new Strings("en").T("feed.player.joined", "Nova"));

    [Fact]
    public void RussianIsRussian() =>
        Assert.Equal("**Nova** зашёл на сервер", new Strings("ru").T("feed.player.joined", "Nova"));

    [Fact]
    public void MissingKeyIsTheKey() => Assert.Equal("no.such.key", new Strings("en").T("no.such.key"));
}
