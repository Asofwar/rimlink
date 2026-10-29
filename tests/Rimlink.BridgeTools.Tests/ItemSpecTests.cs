using Xunit;

namespace Rimlink.BridgeTools.Tests;

public class ItemSpecTests
{
    [Fact]
    public void ParsesDefCountPairs()
    {
        var items = ItemSpec.Parse("Silver:100, Steel:50", out var problems);
        Assert.Empty(problems);
        Assert.Equal([("Silver", 100), ("Steel", 50)], items);
    }

    [Fact]
    public void RepeatedDefsAreSummedIntoOneEntry()
    {
        // Two entries for one def would pick the same stack twice and send more than the map holds.
        var items = ItemSpec.Parse("Steel:10,steel:5,Silver:1,STEEL:1", out var problems);
        Assert.Empty(problems);
        Assert.Equal([("Steel", 16), ("Silver", 1)], items);
    }

    [Fact]
    public void MalformedTokensAreReported()
    {
        var items = ItemSpec.Parse("Steel,Silver:-1,Gold:x,Wood:0,:5,Jade:2", out var problems);
        Assert.Equal([("Jade", 2)], items);
        Assert.Equal(5, problems.Count);
    }
}
