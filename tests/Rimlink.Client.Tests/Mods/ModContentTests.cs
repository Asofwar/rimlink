using Rimlink.Protocol;
using Xunit;

namespace Rimlink.Client.Tests.Mods;

public class ModContentTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public ModContentTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Mod(string name) => Path.Combine(dir, name);

    private static void Write(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void HashCoversDefsPatchesAndAssembliesAtAnyDepth()
    {
        string mod = Mod("a");
        Write(mod, "Defs/a.xml", "<Defs/>");
        Write(mod, "1.7/Patches/p.xml", "<Patch/>");
        Write(mod, "Common/Assemblies/x.dll", "MZ");

        var before = ModContent.Scan(mod);
        Write(mod, "1.7/Patches/p.xml", "<Patch><Operation/></Patch>");
        var after = ModContent.Scan(mod);

        Assert.True(before.HasDefs);
        Assert.True(before.HasPatches);
        Assert.True(before.HasAssemblies);
        Assert.NotEqual(before.FilesHash, after.FilesHash);
    }

    [Fact]
    public void OtherFoldersDoNotChangeTheHash()
    {
        string mod = Mod("b");
        Write(mod, "Defs/a.xml", "<Defs/>");
        var before = ModContent.Scan(mod);

        Write(mod, "Textures/t.png", "png");
        Write(mod, "Languages/English/Keyed/k.xml", "<LanguageData/>");
        Write(mod, "About/About.xml", "<ModMetaData/>");

        Assert.Equal(before.FilesHash, ModContent.Scan(mod).FilesHash);
    }

    [Fact]
    public void AModOfTexturesAndTranslationsHasNoFlags()
    {
        string mod = Mod("c");
        Write(mod, "Textures/t.png", "png");
        Write(mod, "Languages/Russian/DefInjected/ThingDef/x.xml", "<LanguageData/>");

        var info = ModContent.Scan(mod);

        Assert.False(info.HasDefs);
        Assert.False(info.HasPatches);
        Assert.False(info.HasAssemblies);
    }

    [Fact]
    public void FolderNamesAreCaseInsensitiveAndOrderIndependent()
    {
        string first = Mod("d1");
        Write(first, "Defs/a.xml", "A");
        Write(first, "Defs/b.xml", "B");
        string second = Mod("d2");
        Write(second, "defs/b.xml", "B");
        Write(second, "defs/a.xml", "A");

        Assert.Equal(ModContent.Scan(first).FilesHash, ModContent.Scan(second).FilesHash);
    }

    [Fact]
    public void RenamingAFileChangesTheHash()
    {
        string first = Mod("e1");
        Write(first, "Defs/a.xml", "A");
        string second = Mod("e2");
        Write(second, "Defs/renamed.xml", "A");

        Assert.NotEqual(ModContent.Scan(first).FilesHash, ModContent.Scan(second).FilesHash);
    }

    [Fact]
    public void ModsWithoutCountedFilesShareTheEmptyHash()
    {
        string first = Mod("f1");
        Write(first, "About/About.xml", "<ModMetaData/>");
        string second = Mod("f2");
        Directory.CreateDirectory(second);

        var info = ModContent.Scan(first);

        Assert.Equal(ModContent.Scan(second).FilesHash, info.FilesHash);
        Assert.Equal(64, info.FilesHash.Length);
    }

    [Fact]
    public void HashIsLowerHexSha256()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ModContent.Hash("abc"u8.ToArray()));
    }

    [Fact]
    public void SettingsHashIgnoresHowTheGameWroteTheFile()
    {
        byte[] crlf = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<SettingsBlock>\r\n  <ModSettings Class=\"A.B\">\r\n    <points>5</points>\r\n  </ModSettings>\r\n</SettingsBlock>"u8.ToArray();
        byte[] lfWithBom = [0xEF, 0xBB, 0xBF, .. "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SettingsBlock>\n\t<ModSettings Class=\"A.B\">\n\t\t<points>5</points>\n\t</ModSettings>\n</SettingsBlock>\n"u8.ToArray()];
        byte[] other = "<SettingsBlock><ModSettings Class=\"A.B\"><points>6</points></ModSettings></SettingsBlock>"u8.ToArray();

        Assert.Equal(ModContent.SettingsHash(crlf), ModContent.SettingsHash(lfWithBom));
        Assert.NotEqual(ModContent.SettingsHash(crlf), ModContent.SettingsHash(other));
    }

    [Fact]
    public void SettingsHashOfAFileThatIsNotXmlIsTheRawHash()
    {
        byte[] content = "not xml at all"u8.ToArray();

        Assert.Equal(ModContent.Hash(content), ModContent.SettingsHash(content));
    }

    [Fact]
    public void AMissingFolderThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ModContent.Scan(Mod("nope")));
    }
}
