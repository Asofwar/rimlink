extern alias tools;
using Rimlink.Protocol;
using tools::Rimlink.Tools.Commands;
using Xunit;

namespace Rimlink.Server.Tests.Tools;

public class ModpackConfigsCommandTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public ModpackConfigsCommandTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Write(string relative, string content)
    {
        string path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string About(string packageId, string version = "1.0") =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><ModMetaData><packageId>{packageId}</packageId><modVersion>{version}</modVersion></ModMetaData>";

    [Fact]
    public void ImportFillsFilesHash()
    {
        Write("Mods/One/About/About.xml", About("Author.One"));
        Write("Mods/One/Defs/things.xml", "<Defs/>");
        string config = Write("Config/ModsConfig.xml", "<ModsConfigData><activeMods><li>author.one</li><li>author.absent</li></activeMods></ModsConfigData>");

        var entries = ModpackCommand.Collect(config, [Path.Combine(dir, "Mods")]);

        Assert.Equal(ModContent.Scan(Path.Combine(dir, "Mods", "One")).FilesHash, entries.Single(e => e.PackageId == "author.one").FilesHash);
        Assert.Equal("", entries.Single(e => e.PackageId == "author.absent").FilesHash);
    }

    [Fact]
    public void CollectMapsFolderNamesToPackageIds()
    {
        Write("Mods/2009463077/About/About.xml", About("Author.Mod"));
        Write("Mods/999/About/About.xml", About("Other.Mod"));
        Write("Config/Mod_2009463077_ModSettingsClass.xml", "<SettingsBlock>a</SettingsBlock>");
        Write("Config/Mod_2009463077_Other.xml", "<SettingsBlock>b</SettingsBlock>");
        Write("Config/Mod_999_Other.xml", "<SettingsBlock>c</SettingsBlock>");

        var files = ModpackConfigsCommand.Collect(Path.Combine(dir, "Config"), [Path.Combine(dir, "Mods")], ["author.mod"]);

        Assert.Equal(["ModSettingsClass", "Other"], files.Select(f => f.Handle).Order());
        Assert.All(files, f => Assert.Equal("author.mod", f.PackageId));
        Assert.Equal("<SettingsBlock>a</SettingsBlock>"u8.ToArray(), files.Single(f => f.Handle == "ModSettingsClass").Content);
    }

    [Fact]
    public void CollectFailsForAModWithoutSettingsFiles()
    {
        Write("Mods/One/About/About.xml", About("author.one"));
        Directory.CreateDirectory(Path.Combine(dir, "Config"));

        var ex = Assert.Throws<InvalidOperationException>(() => ModpackConfigsCommand.Collect(Path.Combine(dir, "Config"), [Path.Combine(dir, "Mods")], ["author.one"]));

        Assert.Contains("author.one", ex.Message);
    }

    [Fact]
    public void CollectFailsForAnUnknownPackageId()
    {
        Directory.CreateDirectory(Path.Combine(dir, "Config"));
        Directory.CreateDirectory(Path.Combine(dir, "Mods"));

        var ex = Assert.Throws<InvalidOperationException>(() => ModpackConfigsCommand.Collect(Path.Combine(dir, "Config"), [Path.Combine(dir, "Mods")], ["author.nowhere"]));

        Assert.Contains("author.nowhere", ex.Message);
    }
}
