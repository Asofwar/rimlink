using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Influence;

public sealed record InfluenceClaim(long AllianceId, int Strength, long SourceId, int Distance);
public sealed record InfluenceCell(IReadOnlyList<InfluenceClaim> Contenders)
{
    public bool Disputed => Contenders.Count > 1;
}
public sealed record InfluenceMap(IReadOnlyDictionary<int, InfluenceCell> Cells, int VisitedTiles, int NeighborChecks);

public static class InfluenceCalculator
{
    public static InfluenceMap Calculate(InfluenceSnapshot snapshot, InfluenceGraph graph)
    {
        if (!InfluenceValidation.IsValid(snapshot)) throw new InvalidDataException("invalid influence snapshot");
        var claims = new Dictionary<int, Dictionary<long, InfluenceClaim>>();
        int visits = 0, checks = 0;
        foreach (var source in snapshot.Sources)
        {
            var queue = new Queue<(int Tile, int Distance)>();
            var visited = new HashSet<int> { source.Tile };
            queue.Enqueue((source.Tile, 0));
            while (queue.TryDequeue(out var at))
            {
                if (++visits > InfluenceLimits.MaxVisitedTiles) throw new InvalidDataException("influence visit budget exceeded");
                var tile = graph.Get(at.Tile);
                if (tile.Water) continue;
                if (!claims.TryGetValue(at.Tile, out var groups)) claims.Add(at.Tile, groups = []);
                var next = new InfluenceClaim(source.AllianceId, source.Radius + 1 - at.Distance, source.Id, at.Distance);
                if (!groups.TryGetValue(source.AllianceId, out var previous) || next.Strength > previous.Strength
                    || next.Strength == previous.Strength && (next.Distance < previous.Distance || next.Distance == previous.Distance && next.SourceId < previous.SourceId))
                    groups[source.AllianceId] = next;
                if (at.Distance == source.Radius) continue;
                foreach (int neighbor in tile.Neighbors)
                {
                    if (++checks > InfluenceLimits.MaxNeighborChecks) throw new InvalidDataException("influence adjacency budget exceeded");
                    if (visited.Add(neighbor)) queue.Enqueue((neighbor, at.Distance + 1));
                }
            }
        }
        var cells = new SortedDictionary<int, InfluenceCell>();
        foreach (var (tile, groups) in claims)
        {
            int strength = groups.Values.Max(c => c.Strength);
            cells.Add(tile, new InfluenceCell(groups.Values.Where(c => c.Strength == strength).OrderBy(c => c.AllianceId).ToArray()));
        }
        return new InfluenceMap(cells, visits, checks);
    }
}
