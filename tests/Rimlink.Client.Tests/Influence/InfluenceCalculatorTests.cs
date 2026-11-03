using Rimlink.Client.Influence;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Influence;

public sealed class InfluenceCalculatorTests
{
    internal static InfluenceSnapshot Snapshot(params (long Id, long Guild, int Tile)[] sources)
    {
        var snapshot = new InfluenceSnapshot { WorldInstanceId = Guid.NewGuid().ToString("N"), ProcessInstanceId = Guid.NewGuid().ToString("N"), AlgorithmVersion = 1 };
        foreach (long guild in sources.Select(s => s.Guild).Distinct()) snapshot.Alliances.Add(new InfluenceAlliance { Id = guild, Name = "Guild" + guild, PaletteIndex = (int)(guild % 16) });
        foreach (var s in sources) snapshot.Sources.Add(new InfluenceSource { Id = s.Id, AllianceId = s.Guild, Tile = s.Tile, Radius = 3 });
        return snapshot;
    }

    private static InfluenceGraph Line(int length = 15, int water = -1, bool reverse = false) => new(
        Enumerable.Range(0, length).Select(i => new InfluenceTile(i, i == water, (reverse ? new[] { i + 1, i - 1 } : new[] { i - 1, i + 1 }).Where(n => n >= 0 && n < length).ToArray())));

    [Fact]
    public void RadiusAndWaterDoNotColorAnUnreachableBank()
    {
        var result = InfluenceCalculator.Calculate(Snapshot((1, 1, 3)), Line(water: 5));
        Assert.Contains(0, result.Cells.Keys);
        Assert.Contains(4, result.Cells.Keys);
        Assert.DoesNotContain(5, result.Cells.Keys);
        Assert.DoesNotContain(6, result.Cells.Keys);
        Assert.DoesNotContain(14, result.Cells.Keys);
    }

    [Fact]
    public void EqualGuildsDoNotStackAndNearestTieOnlyUsesSourceIdForTheLabel()
    {
        var result = InfluenceCalculator.Calculate(Snapshot((20, 1, 4), (10, 1, 8)), Line());
        var claim = Assert.Single(result.Cells[6].Contenders);
        Assert.Equal(2, claim.Strength);
        Assert.Equal(2, claim.Distance);
        Assert.Equal(10, claim.SourceId);
    }

    [Fact]
    public void RivalTieIsDisputedAndIndependentOfAllInputOrder()
    {
        var first = Snapshot((20, 1, 4), (10, 2, 8));
        var second = first.Clone(); second.Sources.Clear(); second.Sources.AddRange(first.Sources.Reverse());
        var a = InfluenceCalculator.Calculate(first, Line());
        var b = InfluenceCalculator.Calculate(second, Line(reverse: true));
        Assert.True(a.Cells[6].Disputed);
        Assert.Equal(new long[] { 1, 2 }, a.Cells[6].Contenders.Select(c => c.AllianceId));
        Assert.Equal(1, Assert.Single(a.Cells[4].Contenders).AllianceId);
        foreach (var (tile, cell) in a.Cells) Assert.Equal(cell.Contenders, b.Cells[tile].Contenders);
    }

    [Fact]
    public void DuplicateCenterAndMissingGraphNodeRefuseTheWholeCandidate()
    {
        Assert.Throws<InvalidDataException>(() => InfluenceCalculator.Calculate(Snapshot((1, 1, 3), (2, 2, 3)), Line()));
        Assert.Throws<InvalidDataException>(() => InfluenceCalculator.Calculate(Snapshot((1, 1, 100)), Line()));
        Assert.Empty(InfluenceCalculator.Calculate(Snapshot(), new InfluenceGraph([])).Cells);
    }

    [Fact]
    public void BudgetHoldsAt128SourcesEvenOnTheConservativeDegreeSixTree()
    {
        var nodes = new List<(int Id, List<int> Neighbors)>();
        var roots = new List<int>();
        for (int source = 0; source < InfluenceLimits.MaxSources; source++)
        {
            int root = nodes.Count; roots.Add(root); nodes.Add((root, []));
            var queue = new Queue<(int Id, int Depth)>(); queue.Enqueue((root, 0));
            while (queue.TryDequeue(out var next))
            {
                if (next.Depth == 3) continue;
                for (int i = 0; i < (next.Depth == 0 ? 6 : 5); i++)
                {
                    int child = nodes.Count; nodes.Add((child, [next.Id])); nodes[next.Id].Neighbors.Add(child); queue.Enqueue((child, next.Depth + 1));
                }
            }
        }
        var snapshot = Snapshot(roots.Select((tile, i) => ((long)i + 1, (long)i % 16 + 1, tile)).ToArray());
        var graph = new InfluenceGraph(nodes.Select(n => new InfluenceTile(n.Id, false, n.Neighbors)));
        var result = InfluenceCalculator.Calculate(snapshot, graph);
        Assert.Equal(InfluenceLimits.MaxVisitedTiles, result.VisitedTiles);
        Assert.InRange(result.NeighborChecks, 1, InfluenceLimits.MaxNeighborChecks);
        Assert.Equal(InfluenceLimits.MaxVisitedTiles, result.Cells.Count);
    }
}
