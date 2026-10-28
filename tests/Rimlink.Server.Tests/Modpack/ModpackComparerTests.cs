using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;
using Xunit;

namespace Rimlink.Server.Tests.Modpack;

public class ModpackComparerTests
{
    private static ModpackEntry Required(string id, string version = "", int position = 0) =>
        new() { PackageId = id, Version = version, Kind = EntryKind.Required, Position = position };

    private static ModpackEntry Allowed(string id) => new() { PackageId = id, Kind = EntryKind.Allowed };

    private static ModRef Mod(string id, string version = "") => new() { PackageId = id, Version = version };

    [Fact]
    public void ExactMatchIsNull()
    {
        var server = new[] { Required("ludeon.rimworld", "1.7"), Required("brrainz.harmony", "2.4.3") };
        var client = new[] { Mod("ludeon.rimworld", "1.7"), Mod("brrainz.harmony", "2.4.3") };

        Assert.Null(ModpackComparer.Compare(server, client));
    }

    [Fact]
    public void EmptyServerListDisablesTheCheck()
    {
        Assert.Null(ModpackComparer.Compare([], [Mod("anything")]));
    }

    [Fact]
    public void MissingRequiredIsReported()
    {
        var diff = ModpackComparer.Compare([Required("a.one"), Required("b.two")], [Mod("a.one")]);

        Assert.NotNull(diff);
        Assert.Equal(["b.two"], diff.Missing.Select(m => m.PackageId));
        Assert.Empty(diff.Extra);
        Assert.Equal(2, diff.ServerList.Count);
    }

    [Fact]
    public void MissingAllowedIsFine()
    {
        Assert.Null(ModpackComparer.Compare([Required("a.one"), Allowed("ui.nice")], [Mod("a.one")]));
    }

    [Fact]
    public void SteamSuffixAndCaseAreIgnored()
    {
        var diff = ModpackComparer.Compare([Required("brrainz.harmony")], [Mod("Brrainz.Harmony_steam")]);

        Assert.Null(diff);
    }

    [Fact]
    public void WrongVersionIsReported()
    {
        var diff = ModpackComparer.Compare([Required("a.one", "2.0")], [Mod("a.one", " 1.9 ")]);

        Assert.NotNull(diff);
        var mismatch = Assert.Single(diff.WrongVersion);
        Assert.Equal(("a.one", "1.9", "2.0"), (mismatch.PackageId, mismatch.ClientVersion, mismatch.ServerVersion));
    }

    [Fact]
    public void EmptyServerVersionMatchesAny()
    {
        Assert.Null(ModpackComparer.Compare([Required("a.one", "")], [Mod("a.one", "9.9")]));
    }

    [Fact]
    public void ExtraClientModIsReported()
    {
        var diff = ModpackComparer.Compare([Required("a.one")], [Mod("a.one"), Mod("x.extra", "1")]);

        Assert.NotNull(diff);
        Assert.Equal(["x.extra"], diff.Extra.Select(m => m.PackageId));
    }
}
