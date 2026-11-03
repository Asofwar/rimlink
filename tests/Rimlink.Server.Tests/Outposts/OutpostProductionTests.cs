using System.Text.Json;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostProductionTests
{
    [Theory]
    [InlineData(OutpostProfile.Wood, 80)]
    [InlineData(OutpostProfile.Steel, 40)]
    public void Four_skill10_workers_produce_80_wood_or_40_steel_per_hour(OutpostProfile profile, int stock)
    {
        var result = OutpostProduction.Settle(new(0), Crew(profile, 4), TimeSpan.FromHours(1));
        Assert.Equal(new ProductionState(stock), result.State);
    }

    [Theory]
    [InlineData(OutpostProfile.Wood, 12.5)]
    [InlineData(OutpostProfile.Steel, 25)]
    public void Capacity_reached_at_12_5_or_25_hours(OutpostProfile profile, double hours)
    {
        var before = OutpostProduction.Settle(new(0), Crew(profile, 4), TimeSpan.FromHours(hours) - TimeSpan.FromTicks(1));
        Assert.Equal(999, before.State.Stock);
        Assert.NotEqual("0", before.State.FractionNumerator);
        var full = OutpostProduction.Settle(before.State, Crew(profile, 4), TimeSpan.FromTicks(1));
        Assert.Equal(new ProductionState(1000), full.State);
    }

    [Theory]
    [InlineData(OutpostProfile.Wood, 0, 1f, 10, "0", "1")]
    [InlineData(OutpostProfile.Wood, 20, 1f, 30, "0", "1")]
    [InlineData(OutpostProfile.Steel, 0, 1f, 5, "0", "1")]
    [InlineData(OutpostProfile.Steel, 20, 1f, 15, "0", "1")]
    [InlineData(OutpostProfile.Wood, 10, .5f, 10, "0", "1")]
    [InlineData(OutpostProfile.Steel, 0, .5f, 2, "1", "2")]
    [InlineData(OutpostProfile.Wood, int.MinValue, 2f, 10, "0", "1")]
    [InlineData(OutpostProfile.Wood, int.MaxValue, 1f, 30, "0", "1")]
    [InlineData(OutpostProfile.Wood, 10, .1f, 2, "1", "33554432")]
    public void Exact_rates_and_finite_clamps(OutpostProfile profile, int skill, float health, int stock, string numerator, string denominator)
    {
        var result = OutpostProduction.Settle(new(0), [new(profile, skill, health)], TimeSpan.FromHours(1));
        Assert.Equal(new ProductionState(stock, numerator, denominator), result.State);
    }

    [Fact]
    public void Smallest_binary32_health_is_not_rounded_to_zero()
    {
        var result = OutpostProduction.Settle(new(0), [new(OutpostProfile.Wood, 10, float.Epsilon)], TimeSpan.FromHours(1));
        Assert.Equal(new ProductionState(0, "5", "178405961588244985132285746181186892047843328"), result.State);
    }

    [Fact]
    public void Chunking_and_restart_preserve_exact_fraction()
    {
        var state = new ProductionState(0);
        WorkerRate[] workers = [new(OutpostProfile.Wood, 10, .1f)];
        for (var i = 0; i < 3600; i++)
        {
            state = OutpostProduction.Settle(state, workers, TimeSpan.FromSeconds(1)).State;
            if (i % 37 == 0) state = JsonSerializer.Deserialize<ProductionState>(JsonSerializer.Serialize(state))!;
        }
        Assert.Equal(new ProductionState(2, "1", "33554432"), state);
        Assert.Equal(state, OutpostProduction.Settle(new(0), workers, TimeSpan.FromHours(1)).State);
    }

    [Fact]
    public void One_tick_intervals_accumulate_without_a_nearly_one_item_shortcut()
    {
        var state = OutpostProduction.Settle(new(0), Crew(OutpostProfile.Wood, 1), TimeSpan.FromTicks(1)).State;
        Assert.Equal(new ProductionState(0, "1", "1800000000"), state);
        state = OutpostProduction.Settle(new(0, "1799999999", "1800000000"), Crew(OutpostProfile.Wood, 1), TimeSpan.Zero).State;
        Assert.Equal(new ProductionState(0, "1799999999", "1800000000"), state);
    }

    [Fact]
    public void Full_warehouse_has_no_backlog()
    {
        var crew = Crew(OutpostProfile.Wood, 4);
        var full = OutpostProduction.Settle(new(0), crew, TimeSpan.MaxValue).State;
        Assert.Equal(new ProductionState(1000), full);
        Assert.Equal(full, OutpostProduction.Settle(full, crew, TimeSpan.MaxValue).State);
        // Claim removes only stock; elapsed before that claim is not an input to the next settlement.
        var claimed = full with { Stock = 0 };
        Assert.Equal(new ProductionState(0), OutpostProduction.Settle(claimed, crew, TimeSpan.Zero).State);
        Assert.Equal(new ProductionState(20), OutpostProduction.Settle(claimed, crew, TimeSpan.FromMinutes(15)).State);
    }

    [Fact]
    public void Ordinary_offline_day_is_produced_and_long_downtime_is_bounded()
    {
        Assert.Equal(new ProductionState(240), OutpostProduction.Settle(new(0), Crew(OutpostProfile.Steel, 1), TimeSpan.FromDays(1)).State);
        Assert.Equal(new ProductionState(1000), OutpostProduction.Settle(new(0), Crew(OutpostProfile.Steel, 1), TimeSpan.MaxValue).State);
    }

    [Fact]
    public void Recall_uses_old_roster_then_new_roster_without_repricing_the_past()
    {
        var first = OutpostProduction.Settle(new(0), Crew(OutpostProfile.Wood, 4), TimeSpan.FromMinutes(30)).State;
        var second = OutpostProduction.Settle(first, Crew(OutpostProfile.Wood, 3), TimeSpan.FromMinutes(30)).State;
        Assert.Equal(new ProductionState(70), second);
    }

    [Fact]
    public void No_workers_or_negative_elapsed_preserve_existing_stock_and_fraction()
    {
        var state = new ProductionState(6, "1", "2");
        Assert.Equal(state, OutpostProduction.Settle(state, [], TimeSpan.FromDays(1)).State);
        Assert.Equal(state, OutpostProduction.Settle(state, Crew(OutpostProfile.Wood, 1), TimeSpan.MinValue).State);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void NaN_infinite_or_zero_health_rejected(float health)
    {
        Assert.Throws<ArgumentException>(() => OutpostProduction.Settle(new(0), [new(OutpostProfile.Wood, 10, health)], TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(-1, "0", "1")]
    [InlineData(1001, "0", "1")]
    [InlineData(1000, "1", "2")]
    [InlineData(0, "-1", "2")]
    [InlineData(0, "1", "0")]
    [InlineData(0, "1", "1")]
    [InlineData(0, "2", "4")]
    [InlineData(0, "01", "2")]
    [InlineData(0, "0", "2")]
    [InlineData(0, "1", "7")]
    [InlineData(0, "1", "999999999999999999999999999999999999999999999999999999999999999999999")]
    public void Invalid_or_unbounded_persisted_fraction_is_not_normalized_silently(int stock, string numerator, string denominator)
    {
        Assert.Throws<ArgumentException>(() => OutpostProduction.Settle(new(stock, numerator, denominator), Crew(OutpostProfile.Wood, 1), TimeSpan.Zero));
    }

    [Fact]
    public void Unknown_profile_mixed_profiles_and_unbounded_roster_are_refused()
    {
        Assert.Throws<ArgumentException>(() => OutpostProduction.Settle(new(0), [new((OutpostProfile)99, 10, 1)], TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => OutpostProduction.Settle(new(0), [new(OutpostProfile.Wood, 10, 1), new(OutpostProfile.Steel, 10, 1)], TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => OutpostProduction.Settle(new(0), Crew(OutpostProfile.Wood, 5), TimeSpan.Zero));
    }

    private static WorkerRate[] Crew(OutpostProfile profile, int count) => Enumerable.Repeat(new WorkerRate(profile, 10, 1), count).ToArray();
}
