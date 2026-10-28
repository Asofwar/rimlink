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

        var error = SaveValidator.Validate(path, "Seed1", out string? factionName);

        Assert.Null(error);
        Assert.Equal("Nova", factionName);
    }

    [Fact]
    public void AcceptsTribeStartFaction()
    {
        string path = Write(SaveFixtures.MakeSave("Seed1", "Tribe", factionDef: "PlayerTribe"));

        Assert.Null(SaveValidator.Validate(path, "Seed1", out _));
    }

    [Fact]
    public void AcceptsPlayerFactionWithoutNameYet()
    {
        // A fresh colony: RimWorld writes no <name> for the player faction until the player names it.
        string path = Write(SaveFixtures.MakeSave("Seed1", faction: null));

        var error = SaveValidator.Validate(path, "Seed1", out string? factionName);

        Assert.Null(error);
        Assert.Null(factionName);
    }

    [Fact]
    public void AcceptsPlayerFactionDefNamedByTheScenario()
    {
        string path = Write(SaveFixtures.MakeSave("Seed1", "Clan", factionDef: "ModdedPlayerClan", scenarioFactionDef: "ModdedPlayerClan"));

        Assert.Null(SaveValidator.Validate(path, "Seed1", out string? factionName));
        Assert.Equal("Clan", factionName);
    }

    [Fact]
    public void RejectsOtherWorldSeed()
    {
        string path = Write(SaveFixtures.MakeSave("Seed1", "Nova"));

        Assert.NotNull(SaveValidator.Validate(path, "Seed2", out _));
    }

    [Fact]
    public void RejectsSaveWithoutPlayerFaction()
    {
        string path = Write(SaveFixtures.MakeSave("Seed1", "Raiders", factionDef: "Pirate"));

        Assert.NotNull(SaveValidator.Validate(path, "Seed1", out _));
    }

    [Fact]
    public void RejectsNonXmlAndDtd()
    {
        Assert.NotNull(SaveValidator.Validate(Write(SaveFixtures.Compress("not xml at all"u8.ToArray())), "S", out _));
        Assert.NotNull(SaveValidator.Validate(Write(SaveFixtures.Compress("<!DOCTYPE x [<!ENTITY a \"b\">]><savegame>&a;</savegame>"u8.ToArray())), "S", out _));
        Assert.NotNull(SaveValidator.Validate(Write([1, 2, 3]), "S", out _));
    }
}
