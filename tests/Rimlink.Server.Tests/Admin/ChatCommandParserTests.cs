using Rimlink.Server.Application.Admin;
using Xunit;

namespace Rimlink.Server.Tests.Admin;

public class ChatCommandParserTests
{
    [Fact]
    public void ParsesNameAndArgs()
    {
        var command = ChatCommandParser.Parse("/kick Nova spam bot");

        Assert.NotNull(command);
        Assert.Equal("kick", command.Name);
        Assert.Equal(["Nova", "spam", "bot"], command.Args);
    }

    [Fact]
    public void QuotesKeepSpacesTogether()
    {
        var command = ChatCommandParser.Parse("/ban \"Player 1\" 7 \"too many raids\"");

        Assert.Equal(["Player 1", "7", "too many raids"], command!.Args);
    }

    [Fact]
    public void PlainTextIsNotACommand()
    {
        Assert.Null(ChatCommandParser.Parse("hello /kick"));
        Assert.Null(ChatCommandParser.Parse("/"));
    }
}
