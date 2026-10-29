using System.Text;
using Rimlink.Client.Visits;
using Xunit;

namespace Rimlink.Client.Tests.Visits;

/// <summary>A host's copy is checked against the visitor's mods before it is loaded: the save's own header says what it needs.</summary>
public class SaveModsTests
{
    private const string Header = """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
        	<meta>
        		<gameVersion>1.7.4990 rev1069</gameVersion>
        		<modIds>
        			<li>brrainz.harmony</li>
        			<li>ludeon.rimworld</li>
        			<li>local.s2testdef</li>
        			<li>Asofwar.Rimlink</li>
        		</modIds>
        		<modSteamIds>
        			<li>0</li>
        			<li>0</li>
        			<li>0</li>
        			<li>0</li>
        		</modSteamIds>
        		<modNames>
        			<li>Harmony</li>
        			<li>Core</li>
        			<li>S2 Test Def</li>
        			<li>Rimlink</li>
        		</modNames>
        	</meta>
        	<game>
        		<currentMapIndex>0</currentMapIndex>
        """;

    private static Stream Save(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ModsTheVisitorDoesNotRunAreNamed()
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "brrainz.harmony", "ludeon.rimworld", "asofwar.rimlink" };

        var missing = SaveMods.Missing(Save(Header), active.Contains);

        Assert.Equal(["S2 Test Def"], missing);
    }

    [Fact]
    public void NothingIsMissingWhenEveryModRuns()
    {
        Assert.Empty(SaveMods.Missing(Save(Header), _ => true));
    }

    [Fact]
    public void AModWithoutANameIsNamedByItsId()
    {
        string noNames = Header.Replace("<li>S2 Test Def</li>", "<li></li>");

        Assert.Equal(["local.s2testdef"], SaveMods.Missing(Save(noNames), id => id != "local.s2testdef"));
    }

    [Fact]
    public void AFileWithoutAHeaderIsNotRefused()
    {
        Assert.Empty(SaveMods.Missing(Save("<savegame><game /></savegame>"), _ => false));
    }
}
