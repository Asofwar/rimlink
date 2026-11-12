extern alias tools;
using tools::Rimlink.Tools.Commands;
using Xunit;

namespace Rimlink.Server.Tests.Tools;

public class ArbiterMirrorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rimlink-arbiter-mirror", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void The_copy_follows_the_installed_game_but_keeps_its_own_mods_and_never_the_steam_app_id()
    {
        Write("game/Assembly-CSharp.dll", "build 1"); Write("game/Data/Core/Defs/a.xml", "<Defs/>");
        Write("game/steam_appid.txt", "294100"); Write("game/Mods/Local/About/About.xml", "<ModMetaData/>");
        string source = Path.Combine(root, "game"), copy = Path.Combine(root, "copy");
        Write("copy/Mods/arbiter.mod/About/About.xml", "<ModMetaData/>"); Write("copy/steam_appid.txt", "left by an older copy");
        Write("copy/Stale/old.dll", "gone upstream");

        ArbiterCommand.Mirror(source, copy, top => top is "Mods" or "steam_appid.txt");

        Assert.Equal("build 1", File.ReadAllText(Path.Combine(copy, "Assembly-CSharp.dll")));
        Assert.True(File.Exists(Path.Combine(copy, "Data", "Core", "Defs", "a.xml")));
        Assert.False(File.Exists(Path.Combine(copy, "steam_appid.txt")));
        Assert.False(Directory.Exists(Path.Combine(copy, "Stale")));
        Assert.True(File.Exists(Path.Combine(copy, "Mods", "arbiter.mod", "About", "About.xml")));
        Assert.False(Directory.Exists(Path.Combine(copy, "Mods", "Local")));

        File.WriteAllText(Path.Combine(source, "Assembly-CSharp.dll"), "build 2, longer");
        File.Delete(Path.Combine(source, "Data", "Core", "Defs", "a.xml"));
        ArbiterCommand.Mirror(source, copy, top => top is "Mods" or "steam_appid.txt");
        Assert.Equal("build 2, longer", File.ReadAllText(Path.Combine(copy, "Assembly-CSharp.dll")));
        Assert.False(File.Exists(Path.Combine(copy, "Data", "Core", "Defs", "a.xml")));
    }

    [Fact]
    public void A_home_inside_the_game_or_around_it_is_refused_before_any_copy()
    {
        string game = Path.Combine(Path.GetTempPath(), "rimlink-arbiter-nested", "RimWorld");
        Assert.True(ArbiterCommand.Nested(game, game));
        Assert.True(ArbiterCommand.Nested(Path.Combine(game, "arbiter"), game));
        Assert.True(ArbiterCommand.Nested(Path.GetDirectoryName(game)!, game + Path.DirectorySeparatorChar));
        Assert.False(ArbiterCommand.Nested(game + "-arbiter", game)); // a sibling that only shares the prefix
    }

    [Theory]
    [InlineData("asofwar.rimlink", true)]
    [InlineData("vanillaexpanded.vfe_core-2", true)]
    [InlineData("..", false)]
    [InlineData("a..b", false)]
    [InlineData(".hidden", false)]
    [InlineData("a/b", false)]
    [InlineData("c:\\mods", false)]
    [InlineData("", false)]
    public void A_package_id_from_the_modpack_is_a_plain_folder_name_or_nothing(string id, bool safe) =>
        Assert.Equal(safe, ArbiterCommand.SafePackageId(id));
}
