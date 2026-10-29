using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Hosting;

public class IntegrityOptionsTests
{
    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Auth:AdminLogins:0", "admin");

    [Fact]
    public void DefaultsAreTheSpecValues()
    {
        var o = new IntegrityOptions();

        Assert.Equal(DevModePolicy.AdminsOnly, o.DevMode);
        Assert.Equal(20000, o.WealthWarnPerDay);
        Assert.Equal(100000, o.WealthFreezePerDay);
        Assert.Equal(StorytellerPolicy.Locked, o.StorytellerPolicy);
    }

    [Fact]
    public void FreezeBelowWarnIsInvalid()
    {
        Assert.False(IntegrityOptions.IsValid(new IntegrityOptions { WealthWarnPerDay = 50000, WealthFreezePerDay = 10000 }));
        Assert.True(IntegrityOptions.IsValid(new IntegrityOptions { WealthWarnPerDay = 0, WealthFreezePerDay = 10 }));
        Assert.True(IntegrityOptions.IsValid(new IntegrityOptions { WealthWarnPerDay = 10, WealthFreezePerDay = 0 }));
        Assert.False(IntegrityOptions.IsValid(new IntegrityOptions { WealthWarnPerDay = -1 }));
        Assert.False(IntegrityOptions.IsValid(new IntegrityOptions { WealthFreezePerDay = -1 }));
    }

    [Fact]
    public void ServerRefusesToStartWithInvalidThresholds()
    {
        using var fixture = new ServerFixture()
            .WithSetting("Integrity:WealthWarnPerDay", "50000")
            .WithSetting("Integrity:WealthFreezePerDay", "10");

        var ex = Assert.ThrowsAny<Exception>(() => fixture.Services.GetRequiredService<IOptions<IntegrityOptions>>());

        Assert.Contains("Integrity:WealthFreezePerDay", ex.ToString());
    }

    [Fact]
    public async Task LoginReplySaysWhoMayUseDevMode()
    {
        using (var fixture = Server())
        {
            Assert.True((await fixture.RegisterAndLogin("admin")).DevModeAllowed);
            Assert.False((await fixture.RegisterAndLogin("nova")).DevModeAllowed);
        }
        using (var everyone = Server().WithSetting("Integrity:DevMode", "Everyone"))
        {
            Assert.True((await everyone.RegisterAndLogin("nova")).DevModeAllowed);
        }
    }
}
