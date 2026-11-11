using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildProductionTests
{
    [Theory]
    [InlineData(GuildResource.Steel, 10)]
    [InlineData(GuildResource.Wood, 20)]
    public void Fixed_hourly_yield_needs_no_workers(GuildResource resource, int expected) =>
        Assert.Equal(new GuildStock(expected), GuildProduction.Settle(resource, new(0), TimeSpan.FromHours(1)));

    [Theory]
    [InlineData(GuildResource.Steel)]
    [InlineData(GuildResource.Wood)]
    public void Splitting_elapsed_time_preserves_the_exact_fraction(GuildResource resource)
    {
        var state = new GuildStock(0);
        for (int i = 0; i < 3600; i++) state = GuildProduction.Settle(resource, state, TimeSpan.FromSeconds(1));
        Assert.Equal(GuildProduction.Settle(resource, new(0), TimeSpan.FromHours(1)), state);
        var firstTick = GuildProduction.Settle(resource, new(0), TimeSpan.FromTicks(1));
        var rest = GuildProduction.Settle(resource, firstTick, TimeSpan.FromTicks(TimeSpan.TicksPerHour - 1));
        Assert.Equal(state, rest);
    }

    [Theory]
    [InlineData(GuildResource.Steel, 100)]
    [InlineData(GuildResource.Wood, 50)]
    public void Cap_discards_fraction_and_has_no_hidden_backlog(GuildResource resource, int hours)
    {
        var almostFull = GuildProduction.Settle(resource, new(0), TimeSpan.FromHours(hours) - TimeSpan.FromTicks(1));
        Assert.Equal(999, almostFull.Count);
        Assert.NotEqual(0, almostFull.Remainder);
        Assert.Equal(new(1000), GuildProduction.Settle(resource, almostFull, TimeSpan.FromTicks(1)));
        Assert.Equal(new(1000), GuildProduction.Settle(resource, new(0), TimeSpan.MaxValue));
        Assert.Equal(new(1000), GuildProduction.Settle(resource, new(1000), TimeSpan.MaxValue));
        Assert.Equal(new(990), GuildProduction.Settle(resource, new(990), TimeSpan.Zero));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1001, 0)]
    [InlineData(0, -1)]
    [InlineData(0, TimeSpan.TicksPerHour)]
    [InlineData(1000, 1)]
    public void Corrupt_stock_is_refused_even_when_production_is_paused(int count, long remainder) =>
        Assert.Throws<ArgumentException>(() => GuildProduction.Settle(GuildResource.Steel, new(count, remainder), TimeSpan.Zero, paused: true));

    [Theory]
    [InlineData(GuildResource.Steel)]
    [InlineData(GuildResource.Wood)]
    public void Paused_or_nonpositive_time_preserves_every_unit_and_fraction(GuildResource resource)
    {
        var state = new GuildStock(123, 17);
        Assert.Equal(state, GuildProduction.Settle(resource, state, TimeSpan.MaxValue, paused: true));
        Assert.Equal(state, GuildProduction.Settle(resource, state, TimeSpan.MinValue));
        Assert.Equal(state, GuildProduction.Settle(resource, state, TimeSpan.Zero));
    }

    [Fact]
    public void Unknown_resource_is_refused_without_substituting_another_profile() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GuildProduction.Settle((GuildResource)0, new(0), TimeSpan.FromHours(1)));
}
