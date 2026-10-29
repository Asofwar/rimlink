using Rimlink.Client.Chat;
using Xunit;

namespace Rimlink.Client.Tests.Chat;

/// <summary>Live UX spec §3: [[tile:N]] tokens in chat text, at most three per message, anything malformed stays text.</summary>
public class ChatCardsTests
{
    [Fact]
    public void ParsesOneToken()
    {
        var span = Assert.Single(ChatCards.Parse("go [[tile:85538]] now"));

        Assert.Equal(new CardSpan(3, 14, 85538), span);
    }

    [Fact]
    public void ParsesAtMostThree()
    {
        var spans = ChatCards.Parse("[[tile:1]] [[tile:2]] [[tile:3]] [[tile:4]]");

        Assert.Equal([1, 2, 3], spans.Select(s => s.Tile));
    }

    [Theory]
    [InlineData("[[tile:]]")]
    [InlineData("[[tile:-5]]")]
    [InlineData("[[tile:99999999999]]")]
    [InlineData("[[tile:12")]
    [InlineData("[tile:12]")]
    [InlineData("[[Tile:12]]")]
    [InlineData("")]
    public void MalformedTokensAreText(string text)
    {
        Assert.Empty(ChatCards.Parse(text));
    }

    [Fact]
    public void TokenRoundTrips()
    {
        Assert.Equal("[[tile:7]]", ChatCards.Token(7));
        Assert.Equal(7, ChatCards.Parse(ChatCards.Token(7))[0].Tile);
    }
}
