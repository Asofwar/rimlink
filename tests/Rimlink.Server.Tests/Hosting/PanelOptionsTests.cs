using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Hosting;

public class PanelOptionsTests
{
    [Fact]
    public void DefaultsMatchTheSpec()
    {
        using var fixture = new ServerFixture();

        var o = fixture.Services.GetRequiredService<IOptions<PanelOptions>>().Value;

        Assert.True(o.Enabled);
        Assert.True(o.Public);
        Assert.True(o.PublicPlayerNames);
        Assert.Equal(12, o.SessionHours);
        Assert.Equal("", o.ServerName);
        Assert.Equal("", o.Description);
        Assert.Equal("", o.PublicAddress);
        Assert.Equal("Rimlink", o.DisplayName);
        Assert.Equal(TimeSpan.FromHours(12), o.SessionLength);
    }

    [Fact]
    public void SessionLengthIsClamped()
    {
        Assert.Equal(TimeSpan.FromHours(1), new PanelOptions { SessionHours = 0 }.SessionLength);
        Assert.Equal(TimeSpan.FromHours(168), new PanelOptions { SessionHours = 1000 }.SessionLength);
    }

    [Fact]
    public void DescriptionIsTrimmedAndCut()
    {
        Assert.Equal("Friendly server", new PanelOptions { Description = "  Friendly server \n" }.DescriptionText);
        Assert.Equal(1000, new PanelOptions { Description = new string('x', 1500) }.DescriptionText.Length);
        Assert.Equal("Mine", new PanelOptions { ServerName = " Mine " }.DisplayName);
    }

    [Fact]
    public void ServerVersionIsTheModVersion()
    {
        string modVersion = XDocument.Load(Path.Combine(RepoPaths.Root, "mod", "About", "About.xml")).Root!.Element("modVersion")!.Value;

        Assert.Equal(modVersion, ServerVersion.Current);
    }
}
