using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostAdmissionPreviewTests
{
    [Theory] [InlineData(OutpostProfile.Wood, 80d)] [InlineData(OutpostProfile.Steel, 40d)]
    public void Preview_shows_real_hourly_rate_capacity_and_remaining_wallet(OutpostProfile profile, double expected)
    {
        var p = OutpostAdmissionPreviews.Calculate(profile, Enumerable.Repeat(new WorkerProductionPreview(10, 10, 1, 1, 1), 4).ToArray(), true, 700);
        Assert.Equal(expected, p.UnitsPerHour); Assert.Equal(1000, p.Capacity); Assert.Equal(500, p.Cost); Assert.Equal(200, p.WalletAfter);
    }
    [Fact] public void Preview_uses_selected_skill_and_minimum_capacity_without_staffing_charge()
    {
        var p = OutpostAdmissionPreviews.Calculate(OutpostProfile.Steel, [new(20, 0, 2, 0.5f, 1)], false, 200);
        Assert.Equal(2.5, p.UnitsPerHour); Assert.Equal(0, p.Cost); Assert.Equal(200, p.WalletAfter);
    }
    [Fact] public void Preview_clamps_skills_and_does_not_hide_insufficient_wallet()
    {
        var p = OutpostAdmissionPreviews.Calculate(OutpostProfile.Wood, [new(999, 0, 2, 2, 2)], true, 100);
        Assert.Equal(30, p.UnitsPerHour); Assert.Equal(-400, p.WalletAfter);
    }
    [Theory] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(0f)]
    public void Invalid_capacity_is_not_shown_as_a_positive_rate(float health)
        => Assert.Throws<ArgumentException>(() => OutpostAdmissionPreviews.Calculate(OutpostProfile.Wood, [new(10, 10, health, 1, 1)], true, 700));
}
