using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Economy;

public class EconomyOptionsTests
{
    [Fact]
    public void DefaultsMatchTheSpec()
    {
        using var fixture = new ServerFixture();

        var o = fixture.Services.GetRequiredService<IOptions<EconomyOptions>>().Value;

        Assert.True(o.Enabled);
        Assert.Equal(900 * 1024, o.MaxParcelBytes); // below the 1 MB gRPC message limit
        Assert.Equal(200, o.MaxItemsPerParcel);
        Assert.Equal(10, o.MaxPawnsPerParcel);
        Assert.Equal(20, o.MaxLotsPerPlayer);
        Assert.Equal(50, o.MaxPendingParcels);
        Assert.Equal(30, o.LotLifetimeDays);
        Assert.Equal(0, o.MarketFeePercent);
        Assert.Equal(5, o.SendCooldownSeconds);
        Assert.Equal(100, o.ServerWealthPercent);
        Assert.True(o.BlockOutflowUnderThreat);
        Assert.Equal(0.5, o.StorageFeePercentPerDay);
        Assert.Equal(3, o.StorageFreeDays);
    }

    [Fact]
    public void SettingsOverrideDefaults()
    {
        using var fixture = new ServerFixture().WithSetting("Economy:MarketFeePercent", "10").WithSetting("Economy:Enabled", "false");

        var o = fixture.Services.GetRequiredService<IOptions<EconomyOptions>>().Value;

        Assert.Equal(10, o.MarketFeePercent);
        Assert.False(o.Enabled);
    }

    [Fact]
    public void SafeTradesDefaults()
    {
        var economy = new EconomyOptions();
        Assert.Equal(7, economy.GiftDecisionDays);
        Assert.Equal(7, economy.TradeLifetimeDays);
        Assert.Equal(5, economy.MaxOpenTrades);
        Assert.Equal(200, new SocialOptions().MaxBlocks);
    }

    [Theory]
    [InlineData("Economy:MaxOpenTrades", "MaxOpenTrades")]
    [InlineData("Economy:GiftDecisionDays", "GiftDecisionDays")]
    [InlineData("Economy:TradeLifetimeDays", "TradeLifetimeDays")]
    [InlineData("Social:MaxBlocks", "MaxBlocks")]
    public void SafeTradesSettingsBelowOneFailAtStart(string key, string name)
    {
        using var fixture = new ServerFixture().WithSetting(key, "0");

        Assert.Contains(name, Assert.ThrowsAny<Exception>(() => fixture.CreateClient()).ToString());
    }
}
