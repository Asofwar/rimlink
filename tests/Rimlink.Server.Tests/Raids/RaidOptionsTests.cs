using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Raids;

public class RaidOptionsTests
{
    [Fact]
    public void DefaultsMatchTheSpec()
    {
        using var fixture = new ServerFixture();

        var o = fixture.Services.GetRequiredService<IOptions<RaidOptions>>().Value;

        Assert.True(o.Enabled);
        Assert.Equal(10, o.MaxRaiders);
        Assert.Equal(1, o.MaxActiveRaidsPerAttacker);
        Assert.Equal(8, o.TargetCooldownHours);
        Assert.Equal(24, o.PairCooldownHours);
        Assert.Equal(3, o.NewbieProtectionDays);
        Assert.Equal(20000, o.MinColonyWealth);
        Assert.Equal(10, o.LootCapPercent);
        Assert.Equal(5000, o.LootCapSilver);
        Assert.False(o.AllowKidnap);
        Assert.Equal(60000, o.WarnTicks);
        Assert.Equal(3, o.TimeoutDays);
        Assert.Equal(7, o.ExpireRealDays);
        Assert.Equal(24, o.PeacefulOffDelayHours);
    }

    [Fact]
    public void SettingsOverrideDefaults()
    {
        using var fixture = new ServerFixture().WithSetting("Raids:MaxRaiders", "3").WithSetting("Raids:AllowKidnap", "true");

        var o = fixture.Services.GetRequiredService<IOptions<RaidOptions>>().Value;

        Assert.Equal(3, o.MaxRaiders);
        Assert.True(o.AllowKidnap);
    }
}
