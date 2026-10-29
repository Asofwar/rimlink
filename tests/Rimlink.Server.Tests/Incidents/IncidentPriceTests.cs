using Rimlink.Server.Application.Incidents;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests.Incidents;

/// <summary>Price = BasePrice × (W/100000)^(2/3) × L^(3/2) × multipliers (paid incidents spec §2).</summary>
public class IncidentPriceTests
{
    private static readonly IncidentOptions Options = new();

    private static IncidentCatalogEntry Entry(string id) => IncidentOptions.DefaultCatalog.Single(e => e.Id == id);

    [Fact]
    public void HostilePriceFollowsWealthAndLevel()
    {
        Assert.Equal(100, IncidentPrice.For(Entry("pirates"), level: 2, pods: false, wealth: 100000, Options));
        Assert.Equal(283, IncidentPrice.For(Entry("pirates"), level: 4, pods: false, wealth: 100000, Options));
        Assert.Equal(400, IncidentPrice.For(Entry("pirates"), level: 2, pods: false, wealth: 800000, Options));
    }

    [Fact]
    public void PodsAndTypeMultiply() =>
        Assert.Equal(750, IncidentPrice.For(Entry("mechanoids"), level: 2, pods: true, wealth: 100000, Options));

    [Fact]
    public void WeatherAndKindIgnoreLevel()
    {
        Assert.Equal(200, IncidentPrice.For(Entry("toxic-fallout"), level: 4, pods: false, wealth: 100000, Options));
        Assert.Equal(200, IncidentPrice.For(Entry("wanderer"), level: 1, pods: false, wealth: 100000, Options));
    }

    [Fact]
    public void MinPriceFloors() =>
        Assert.Equal(50, IncidentPrice.For(Entry("pirates"), level: 1, pods: false, wealth: 0, Options));

    [Fact]
    public void PriceSaturates() =>
        Assert.Equal(long.MaxValue, IncidentPrice.For(Entry("mechanoids"), level: 4, pods: true, wealth: long.MaxValue, new IncidentOptions { BasePrice = 1e12 }));

    [Fact]
    public void LevelFactors()
    {
        Assert.Equal([0.5, 1, 1.5, 2], new[] { 1, 2, 3, 4 }.Select(IncidentPrice.LevelFactor).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => IncidentPrice.LevelFactor(5));
    }
}
