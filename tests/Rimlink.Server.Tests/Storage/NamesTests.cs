using Rimlink.Server.Storage;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public class NamesTests
{
    [Theory]
    [InlineData("Nova")]
    [InlineData("Игрок 1")]
    [InlineData("J.R.'s")]
    [InlineData("ab")]
    public void AcceptsValidNames(string name) => Assert.True(Names.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 33
    [InlineData("a\nb")]
    [InlineData("../x")]
    [InlineData("a<b>")]
    public void RejectsInvalidNames(string? name) => Assert.False(Names.IsValid(name));
}
