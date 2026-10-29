using Rimlink.Client.Mods;
using Xunit;

namespace Rimlink.Client.Tests.Mods;

public class SettingsRedirectTests
{
    private static readonly ServerSettingsStore Store = new(Path.Combine("root", "server-settings"));
    private static readonly SettingsKey Below = new("a.below", "BelowMod");
    private static readonly SettingsKey Above = new("b.above", "AboveMod");

    private static SettingsRedirect Redirect() =>
        new(Store, new ServerSettingsSet("s_1", "s", [Below, Above]), readBeforeRimlink: [Above]);

    [Fact]
    public void AModLoadedAfterRimlinkUsesTheServerCopy()
    {
        Assert.Equal(Store.PathFor("s_1", Below), Redirect().Resolve(Below, "own.xml"));
    }

    [Fact]
    public void AModCreatedBeforeRimlinkKeepsItsOwnFileForReadsWritesAndTheLoginHash()
    {
        Assert.Equal("own.xml", Redirect().Resolve(Above, "own.xml"));
    }

    [Fact]
    public void ModsOutsideTheSetKeepTheirOwnFile()
    {
        Assert.Equal("own.xml", Redirect().Resolve(new SettingsKey("c.other", "OtherMod"), "own.xml"));
    }
}
