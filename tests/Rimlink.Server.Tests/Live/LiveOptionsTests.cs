using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Live;

public class LiveOptionsTests
{
    [Fact]
    public void DefaultsMatchTheSpec()
    {
        using var fixture = new ServerFixture();

        var o = fixture.Services.GetRequiredService<IOptions<LiveOptions>>().Value;

        Assert.True(o.Enabled);
        Assert.Equal(60, o.InviteSeconds);
        Assert.Equal(5, o.ReconnectWaitMinutes);
        Assert.Equal(10, o.GuestPauses);
        Assert.Equal(60, o.GuestPauseSeconds);
        Assert.Equal(262144, o.MaxFrameBytes);
        Assert.Equal(20, o.DeltasPerSecond);
        Assert.Equal(30, o.OrdersPerSecond);
        Assert.Equal(10, o.HeartbeatTimeoutSeconds);
    }

    [Fact]
    public void SettingsOverrideDefaults()
    {
        using var fixture = new ServerFixture().WithSetting("Live:InviteSeconds", "2").WithSetting("Live:Enabled", "false");

        var o = fixture.Services.GetRequiredService<IOptions<LiveOptions>>().Value;

        Assert.Equal(2, o.InviteSeconds);
        Assert.False(o.Enabled);
    }
}
