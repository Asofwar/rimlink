using Xunit;

namespace Rimlink.BridgeTools.Tests;

public sealed class ColonyCoverageTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Default_preserves_existing_generation_flow(bool isAdmin, bool worldExists)
    {
        Assert.Null(ColonyCoverage.Refusal(0f, isAdmin, worldExists, false));
    }

    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Native_normal_coverages_are_allowed_for_first_admin_world(float coverage)
    {
        Assert.Null(ColonyCoverage.Refusal(coverage, true, false, false));
    }

    [Fact]
    public void Native_five_percent_option_requires_actual_dev_mode()
    {
        Assert.NotNull(ColonyCoverage.Refusal(0.05f, true, false, false));
        Assert.Null(ColonyCoverage.Refusal(0.05f, true, false, true));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(0.1f)]
    [InlineData(0.4f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Unavailable_native_options_are_refused(float coverage)
    {
        Assert.NotNull(ColonyCoverage.Refusal(coverage, true, false, true));
    }

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void An_override_cannot_change_a_locked_world_or_a_nonadmin_generation(float coverage)
    {
        Assert.NotNull(ColonyCoverage.Refusal(coverage, true, true, true));
        Assert.NotNull(ColonyCoverage.Refusal(coverage, false, false, true));
    }
}
