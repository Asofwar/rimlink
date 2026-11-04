using System.Security.Cryptography;
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

        Assert.Equal(new SaveMeta("greengrow", "Nova's", 4321, 123456, 65432, DevMode: true), meta with { RawSha256 = null });
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(path)), meta.RawSha256);
    }

    [Fact]
    public void AnOlderSaveHasNoWealthAndNoFlag()
    {
        string path = Rws("");
        var meta = SaveMetaReader.Read(path);

        Assert.Equal(new SaveMeta("greengrow", "Nova's", 4321), meta with { RawSha256 = null });
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(path)), meta.RawSha256);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(3L)]
    [InlineData(4_294_967_296L)]
    public void ReadsTheGenerationWrittenByTheGame(long generation)
    {
        var meta = SaveMetaReader.Read(Rws($"""
            <li Class="Rimlink.Client.Game.RimlinkGameComponent">
              <rimlinkSaveGeneration>{generation}</rimlinkSaveGeneration>
            </li>
            """));

        Assert.Equal(generation, meta.SaveGeneration);
    }

    [Fact]
    public void ALocalFileWrittenBeforeGenerationsBelongsToZero()
    {
        Assert.Equal(0, SaveMetaReader.Read(Rws("")).SaveGeneration);
    }
}
