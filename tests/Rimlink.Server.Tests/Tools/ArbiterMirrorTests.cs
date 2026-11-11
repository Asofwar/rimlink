extern alias tools;
using tools::Rimlink.Tools.Commands;
using Xunit;

namespace Rimlink.Server.Tests.Tools;

public class ArbiterMirrorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rimlink-arbiter-mirror", Guid.NewGuid().ToString("N"));
    public void Dispose() => Directory.Delete(root, true);

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
}
