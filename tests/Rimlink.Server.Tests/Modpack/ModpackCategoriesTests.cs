using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;
using Xunit;

namespace Rimlink.Server.Tests.Modpack;

/// <summary>The categories a newer client gets (files, order, auto-allowed mods, settings, who fixes a version).</summary>
public class ModpackCategoriesTests
{
    private static ModpackEntry Required(string id, int position, string version = "", string filesHash = "") =>
        new() { PackageId = id, Version = version, Kind = EntryKind.Required, Position = position, FilesHash = filesHash };

    private static ModpackEntry Allowed(string id, int position) => new() { PackageId = id, Kind = EntryKind.Allowed, Position = position };

    /// <summary>A mod as a new client reports it (files hash set unless given empty).</summary>
    private static ModRef Mod(string id, int position, string version = "", string filesHash = "h", bool defs = false, bool patches = false, bool assemblies = false) =>
        new() { PackageId = id, Position = position, Version = version, FilesHash = filesHash, HasDefs = defs, HasPatches = patches, HasAssemblies = assemblies };

    private static ModConfig Config(string id, string handle, string content) =>
        new() { PackageId = id, Handle = handle, Content = ByteString.CopyFromUtf8(content), Hash = Rimlink.Protocol.ModContent.SettingsHash(System.Text.Encoding.UTF8.GetBytes(content)) };

    private static ModSettingsHash Settings(string id, string handle, string hash) => new() { PackageId = id, Handle = handle, Hash = hash };

    [Theory]
    [InlineData("1.2.0", "1.1.9", FixSide.Server)]
    [InlineData("1.10", "1.9", FixSide.Server)]
    [InlineData("1.1", "1.2", FixSide.Player)]
    [InlineData("1.2-beta", "1.2", FixSide.Player)]
    [InlineData("2", "10", FixSide.Player)]
    public void VersionSideSaysWhoIsBehind(string client, string server, FixSide side)
    {
        var diff = ModpackComparer.Compare([Required("a.one", 0, server)], [Mod("a.one", 0, client)]);

        Assert.Equal(side, Assert.Single(diff!.WrongVersion).Side);
    }

    [Fact]
    public void DifferentFilesOfTheSameVersionAreReported()
    {
        var diff = ModpackComparer.Compare([Required("a.one", 0, "1.0", "aaa")], [Mod("a.one", 0, "1.0", "bbb")]);

        var files = Assert.Single(diff!.Files);
        Assert.Equal(("a.one", "1.0"), (files.PackageId, files.Version));
        Assert.Empty(diff.WrongVersion);
    }

    [Theory]
    [InlineData("", "bbb")]
    [InlineData("aaa", "")]
    [InlineData("aaa", "aaa")]
    public void FilesAreComparedOnlyWhenBothHashesExistAndDiffer(string server, string client)
    {
        Assert.Null(ModpackComparer.Compare([Required("a.one", 0, "", server), Required("z.last", 1)], [Mod("a.one", 0, "", client), Mod("z.last", 1)]));
    }

    [Fact]
    public void AWrongVersionIsNotAlsoAFilesDifference()
    {
        var diff = ModpackComparer.Compare([Required("a.one", 0, "2.0", "aaa")], [Mod("a.one", 0, "1.0", "bbb")]);

        Assert.Single(diff!.WrongVersion);
        Assert.Empty(diff.Files);
    }

    [Fact]
    public void ContentFreeExtraModsAreAllowedByThemselves()
    {
        Assert.Null(ModpackComparer.Compare([Required("a.one", 0)], [Mod("a.one", 0), Mod("x.translation", 1)]));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ExtraModsWithDefsPatchesOrCodeAreStillExtra(bool defs, bool patches, bool assemblies)
    {
        var diff = ModpackComparer.Compare([Required("a.one", 0)], [Mod("a.one", 0), Mod("x.extra", 1, defs: defs, patches: patches, assemblies: assemblies)]);

        Assert.Equal(["x.extra"], diff!.Extra.Select(m => m.PackageId));
    }

    [Fact]
    public void AnOldClientGetsNoAutoAllowanceNoOrderAndNoFilesCheck()
    {
        var server = new[] { Required("a.one", 0, "", "aaa"), Required("b.two", 1) };
        var client = new[] { Mod("b.two", 0, filesHash: ""), Mod("a.one", 0, filesHash: ""), Mod("x.translation", 0, filesHash: "") };

        var diff = ModpackComparer.Compare(server, client);

        Assert.Equal(["x.translation"], diff!.Extra.Select(m => m.PackageId));
        Assert.False(diff.OrderDiffers);
        Assert.Empty(diff.Files);
    }

    [Fact]
    public void SwappedRequiredModsAreAnOrderDifferenceOnly()
    {
        var diff = ModpackComparer.Compare([Required("a.one", 0), Required("b.two", 1), Required("c.three", 2)], [Mod("a.one", 0), Mod("c.three", 1), Mod("b.two", 2)]);

        Assert.NotNull(diff);
        Assert.True(diff.OrderDiffers);
        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Extra);
        Assert.Empty(diff.WrongVersion);
        Assert.Empty(diff.Files);
        Assert.Empty(diff.Configs);
    }

    [Fact]
    public void ModsOutsideTheRequiredSetDoNotCountForOrder()
    {
        var server = new[] { Required("a.one", 0), Allowed("ui.nice", 1), Required("b.two", 2), Required("c.three", 3) };
        var client = new[] { Mod("ui.nice", 0, assemblies: true), Mod("a.one", 1), Mod("x.translation", 2), Mod("b.two", 3), Mod("c.three", 4) };

        Assert.Null(ModpackComparer.Compare(server, client));
    }

    [Fact]
    public void OrderIgnoresSteamSuffixesAndMissingMods()
    {
        var server = new[] { Required("a.one", 0), Required("b.two", 1), Required("c.three", 2) };
        var client = new[] { Mod("A.One_steam", 0), Mod("c.three", 1) };

        var diff = ModpackComparer.Compare(server, client);

        Assert.Equal(["b.two"], diff!.Missing.Select(m => m.PackageId));
        Assert.False(diff.OrderDiffers);
    }

    [Fact]
    public void DifferentSettingsBringTheServerFileWhole()
    {
        var config = Config("a.one", "OneMod", "<SettingsBlock>server</SettingsBlock>");

        var diff = ModpackComparer.Compare([Required("a.one", 0)], [Mod("a.one", 0)], [config], [Settings("a.one", "OneMod", "other")]);

        var sent = Assert.Single(diff!.Configs);
        Assert.Equal(config.Content, sent.Content);
        Assert.Equal(config.Hash, sent.Hash);
    }

    [Fact]
    public void OneDifferentSettingsFileBringsTheWholeSetSoTheClientCanReplaceIt()
    {
        var a = Config("a.one", "OneMod", "<a/>");
        var b = Config("b.two", "TwoMod", "<b/>");

        var diff = ModpackComparer.Compare([Required("a.one", 0), Required("b.two", 1)], [Mod("a.one", 0), Mod("b.two", 1)], [a, b],
            [Settings("a.one", "OneMod", "changed"), Settings("b.two", "TwoMod", b.Hash)]);

        Assert.Equal(["a.one", "b.two"], diff!.Configs.Select(c => c.PackageId));
    }

    [Fact]
    public void EqualSettingsPass()
    {
        var config = Config("a.one", "OneMod", "<SettingsBlock/>");

        Assert.Null(ModpackComparer.Compare([Required("a.one", 0)], [Mod("a.one", 0)], [config], [Settings("A.One", "OneMod", config.Hash)]));
    }

    [Fact]
    public void SettingsOfAModTheClientLacksAreNotSent()
    {
        var config = Config("b.two", "TwoMod", "<SettingsBlock/>");

        var diff = ModpackComparer.Compare([Required("a.one", 0), Required("b.two", 1)], [Mod("a.one", 0)], [config], [Settings("a.one", "OneMod", "none")]);

        Assert.Single(diff!.Missing);
        Assert.Empty(diff.Configs);
    }

    [Fact]
    public void SettingsAreNotCheckedWhenTheClientSentNone()
    {
        var config = Config("a.one", "OneMod", "<SettingsBlock/>");

        Assert.Null(ModpackComparer.Compare([Required("a.one", 0)], [Mod("a.one", 0)], [config], []));
    }

    [Fact]
    public void AModClassWithoutAFileGetsTheServerFile()
    {
        var config = Config("a.one", "OneMod", "<SettingsBlock/>");

        var diff = ModpackComparer.Compare([Required("a.one", 0)], [Mod("a.one", 0)], [config], [Settings("a.one", "OneMod", Rimlink.Protocol.ModContent.NoSettings)]);

        Assert.Single(diff!.Configs);
    }
}
