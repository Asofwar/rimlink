using Rimlink.Server.Application.Modpack;
using Xunit;

namespace Rimlink.Server.Tests.Tools;

public class DefScannerTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public DefScannerTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void ScansThingAndPawnKindDefsSkippingAbstractOnes()
    {
        string a = Path.Combine(dir, "Mods", "A", "Defs");
        string b = Path.Combine(dir, "Mods", "B", "1.7", "Defs", "Pawns");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "Things.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Defs>
              <ThingDef Name="ResourceBase" Abstract="True"><defName>ResourceBase</defName></ThingDef>
              <ThingDef ParentName="ResourceBase"><defName>Steel</defName><label>steel</label></ThingDef>
              <RecipeDef><defName>Make_Steel</defName></RecipeDef>
            </Defs>
            """);
        File.WriteAllText(Path.Combine(b, "Pawns.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Defs>
              <PawnKindDef><defName>Colonist</defName></PawnKindDef>
            </Defs>
            """);

        var defs = DefScanner.Scan([Path.Combine(dir, "Mods")]);

        Assert.Equal(["Colonist", "Steel"], defs);
    }

    [Fact]
    public void IgnoresUnreadableXml()
    {
        string a = Path.Combine(dir, "Mods", "Broken", "Defs");
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(a, "Bad.xml"), "<Defs><ThingDef><defName>Half");
        File.WriteAllText(Path.Combine(a, "Good.xml"), "<Defs><ThingDef><defName>Gold</defName></ThingDef></Defs>");

        Assert.Equal(["Gold"], DefScanner.Scan([Path.Combine(dir, "Mods")]));
    }
}
