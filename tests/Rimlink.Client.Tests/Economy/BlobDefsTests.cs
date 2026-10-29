using Rimlink.Client.Economy;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Economy;

/// <summary>A parcel from a game with mods this one lacks names what is missing instead of failing on an anonymous blob.</summary>
public class BlobDefsTests
{
    private static readonly HashSet<string> Things = ["Steel", "Silver", "WoodLog", "Apparel_Parka"];
    private static readonly HashSet<string> Kinds = ["Colonist", "Muffalo"];

    private static IReadOnlyList<string> Missing(ItemList? items, PawnList? pawns) => BlobDefs.Missing(items, pawns, Things.Contains, Kinds.Contains);

    private static ItemList Items(params ItemBlob[] blobs)
    {
        var list = new ItemList();
        list.Items.AddRange(blobs);
        return list;
    }

    [Fact]
    public void KnownDefsAreNotMissing()
    {
        var pawns = new PawnList();
        pawns.Pawns.Add(new PawnBlob { KindDef = "Colonist", Name = "Charlie" });

        Assert.Empty(Missing(Items(new ItemBlob { DefName = "Apparel_Parka", StuffDef = "WoodLog", Label = "parka" }), pawns));
        Assert.Empty(Missing(null, null));
    }

    [Fact]
    public void AMissingItemDefIsNamedWithItsLabel()
    {
        var missing = Missing(Items(new ItemBlob { DefName = "Steel" }, new ItemBlob { DefName = "S2TestWidget", Label = "s2 test widget x10" }), null);

        Assert.Equal(["s2 test widget x10 (S2TestWidget)"], missing);
    }

    [Fact]
    public void ADefIsNamedOnceWhateverItsStacksAreLabelled()
    {
        var missing = Missing(Items(new ItemBlob { DefName = "ModRifle", Label = "rifle (good)" }, new ItemBlob { DefName = "ModRifle", Label = "rifle (normal) 85%" }), null);

        Assert.Equal(["rifle (good) (ModRifle)"], missing);
    }

    [Fact]
    public void AMissingStuffAndAMissingPawnKindAreNamedOnceEach()
    {
        var pawns = new PawnList();
        pawns.Pawns.Add(new PawnBlob { KindDef = "Ghoul_Modded", Name = "Lurk" });
        pawns.Pawns.Add(new PawnBlob { KindDef = "Ghoul_Modded", Name = "Lurk" });
        var items = Items(new ItemBlob { DefName = "Apparel_Parka", StuffDef = "Leather_Modded", Label = "parka" },
            new ItemBlob { DefName = "Apparel_Parka", StuffDef = "Leather_Modded", Label = "parka" });

        var missing = Missing(items, pawns);

        Assert.Equal(["Leather_Modded", "Lurk (Ghoul_Modded)"], missing);
    }
}
