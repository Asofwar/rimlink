using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Hosting;

public class HygieneOptionsTests
{
    [Fact]
    public void DefaultsMatchTheSpec()
    {
        using var fixture = new ServerFixture();

        var o = fixture.Services.GetRequiredService<IOptions<HygieneOptions>>().Value;

        Assert.Equal(14, o.ArchiveAfterDays);
        Assert.Equal(100000, o.ArchiveMaxWealth);
        Assert.Equal(60, o.ArchiveMaxColonyDays);
        Assert.Equal(6, o.SweepHours);
        Assert.Equal([50000L, 150000L, 400000L, 1000000L], o.Tiers);
    }

    [Fact]
    public void ConfiguredTiersReplaceTheDefaults()
    {
        using var fixture = new ServerFixture()
            .WithSetting("Hygiene:WealthTiers:0", "10").WithSetting("Hygiene:WealthTiers:1", "20")
            .WithSetting("Hygiene:WealthTiers:2", "30").WithSetting("Hygiene:WealthTiers:3", "40");

        Assert.Equal([10L, 20L, 30L, 40L], fixture.Services.GetRequiredService<IOptions<HygieneOptions>>().Value.Tiers);
    }

    [Theory]
    [InlineData("Hygiene:SweepHours", "0")]
    [InlineData("Hygiene:SweepHours", "169")]
    [InlineData("Hygiene:ArchiveAfterDays", "-1")]
    [InlineData("Hygiene:ArchiveMaxWealth", "-5")]
    [InlineData("Hygiene:ArchiveMaxColonyDays", "-1")]
    [InlineData("Hygiene:WealthTiers:1", "10")]
    public void BadValuesStopTheServer(string key, string value)
    {
        using var fixture = new ServerFixture().WithSetting(key, value);

        Assert.Contains("Hygiene", Assert.ThrowsAny<Exception>(() => fixture.CreateClient()).ToString());
    }
}
