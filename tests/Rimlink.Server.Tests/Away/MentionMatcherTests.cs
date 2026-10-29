using Rimlink.Server.Application.Away;
using Xunit;

namespace Rimlink.Server.Tests.Away;

public class MentionMatcherTests
{
    [Theory]
    [InlineData("hi Nova", "Nova", true)]
    [InlineData("hi @nova!", "Nova", true)]
    [InlineData("NOVA, come here", "Nova", true)]
    [InlineData("Novak here", "Nova", false)]
    [InlineData("supernova", "Nova", false)]
    [InlineData("Привет, Мира", "мира", true)]
    [InlineData("Мирал пришёл", "Мира", false)]
    [InlineData("a.b+c said hi", "a.b+c", true)]
    [InlineData("xa.b+c", "a.b+c", false)]
    [InlineData("Al", "Al", true)]
    [InlineData("Alpha", "Al", false)]
    [InlineData("Alpha and Al", "Al", true)]
    [InlineData("Nova2 is not Nova", "Nova", true)]
    [InlineData("Nova2", "Nova", false)]
    [InlineData("", "Nova", false)]
    [InlineData("Nova", "", false)]
    public void MentionMatcherCases(string text, string name, bool expected) =>
        Assert.Equal(expected, MentionMatcher.Mentions(text, name));
}
