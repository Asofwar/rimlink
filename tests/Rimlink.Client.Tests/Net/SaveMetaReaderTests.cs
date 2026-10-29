using Rimlink.Client.Net;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class SaveMetaReaderTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public SaveMetaReaderTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }

    private string Rws(string components)
    {
        string path = Path.Combine(dir, "save.rws");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <savegame>
              <meta><gameVersion>1.7</gameVersion></meta>
              <game>
                <tickManager><ticksGame>4321</ticksGame></tickManager>
                <world>
                  <info><seedString>greengrow</seedString></info>
                  <factionManager><allFactions>
                    <li><def>Pirate</def><name>Raiders</name></li>
                    <li><def>PlayerColony</def><name>Nova's</name></li>
                  </allFactions></factionManager>
                </world>
                <maps><li><padding>xxxx</padding></li></maps>
                <components>{components}</components>
              </game>
            </savegame>
            """);
        return path;
    }

    [Fact]
    public void ReadsTheHeaderAndTheRimlinkComponent()
    {
        string path = Rws($"""
            <li Class="Rimlink.Client.Game.RimlinkGameComponent">
              <{SaveMetaReader.DevModeNode}>True</{SaveMetaReader.DevModeNode}>
              <{SaveMetaReader.WealthTotalNode}>123456</{SaveMetaReader.WealthTotalNode}>
              <{SaveMetaReader.WealthItemsNode}>65432</{SaveMetaReader.WealthItemsNode}>
            </li>
            """);

        var meta = SaveMetaReader.Read(path);

        Assert.Equal(new SaveMeta("greengrow", "Nova's", 4321, 123456, 65432, DevMode: true), meta);
    }

    [Fact]
    public void AnOlderSaveHasNoWealthAndNoFlag()
    {
        var meta = SaveMetaReader.Read(Rws(""));

        Assert.Equal(new SaveMeta("greengrow", "Nova's", 4321), meta);
    }
}
