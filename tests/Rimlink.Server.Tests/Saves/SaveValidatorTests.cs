using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Saves;

public class SaveValidatorTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public SaveValidatorTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Write(byte[] data)
    {
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".zst");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void AcceptsMatchingSeedAndFaction()
    {
        string path = Write(SaveFixtures.MakeSave("Seed1", "Nova"));

        var error = SaveValidator.Validate(path, "Seed1", "Nova");

        Assert.Null(error);
    }

    [Theory]
    [InlineData("Seed1", "Nova", "Seed2", "Nova")]
    [InlineData("Seed1", "Nova", "Seed1", "Bob")]
    public void RejectsMismatch(string seed, string faction, string expectedSeed, string expectedFaction)
    {
        string path = Write(SaveFixtures.MakeSave(seed, faction));

        Assert.NotNull(SaveValidator.Validate(path, expectedSeed, expectedFaction));
    }

    [Fact]
    public void RejectsNonXmlAndDtd()
    {
        Assert.NotNull(SaveValidator.Validate(Write(SaveFixtures.Compress("not xml at all"u8.ToArray())), "S", "F"));
        Assert.NotNull(SaveValidator.Validate(Write(SaveFixtures.Compress("<!DOCTYPE x [<!ENTITY a \"b\">]><savegame>&a;</savegame>"u8.ToArray())), "S", "F"));
        Assert.NotNull(SaveValidator.Validate(Write([1, 2, 3]), "S", "F"));
    }
}
