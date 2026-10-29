using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;

namespace Rimlink.Server.Tests.Economy;

public class LotSummaryTests
{
    [Fact]
    public void ItemsAreLabelledWithCounts() =>
        Assert.Equal("Steel ×50, Component ×3", LotSummary.Of(Items(("Steel", 50), ("Component", 3)), null));

    [Fact]
    public void SingleItemHasNoCount() =>
        Assert.Equal("Rifle", LotSummary.Of(Items(("Rifle", 1)), null));

    [Fact]
    public void EmptyLabelFallsBackToDefName()
    {
        var items = Items(("Steel", 2));
        items.Items[0].Label = "";

        Assert.Equal("Steel ×2", LotSummary.Of(items, null));
    }

    [Fact]
    public void PawnLotUsesThePawnLabel()
    {
        var pawn = Pawn("Colonist", "Mira");
        pawn.Label = "Mira, engineer";

        Assert.Equal("Mira, engineer", LotSummary.Of(null, pawn));
        pawn.Label = "";
        Assert.Equal("Mira", LotSummary.Of(new ItemList(), pawn));
    }

    [Fact]
    public void LongSummaryIsCut()
    {
        var items = new ItemList();
        for (int i = 0; i < 30; i++) items.Items.Add(Item($"LongThingName{i:00}", 10));

        string summary = LotSummary.Of(items, null);

        Assert.Equal(200, summary.Length);
        Assert.EndsWith("…", summary);
    }
}
