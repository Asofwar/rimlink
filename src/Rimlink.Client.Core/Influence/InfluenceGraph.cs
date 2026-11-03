using Rimlink.Protocol;

namespace Rimlink.Client.Influence;

public sealed record InfluenceTile(int Id, bool Water, IReadOnlyList<int> Neighbors);

/// <summary>A bounded, immutable copy of nearby surface topology. No game objects cross this boundary.</summary>
public sealed class InfluenceGraph
{
    private readonly Dictionary<int, InfluenceTile> tiles = [];
    public IReadOnlyDictionary<int, InfluenceTile> Tiles => tiles;

    public InfluenceGraph(IEnumerable<InfluenceTile> input)
    {
        foreach (var tile in input)
        {
            if (tiles.Count >= InfluenceLimits.MaxVisitedTiles || tile.Id < 0 || tile.Neighbors.Count > 6
                || tile.Neighbors.Any(n => n < 0 || n == tile.Id) || tile.Neighbors.Distinct().Count() != tile.Neighbors.Count
                || !tiles.TryAdd(tile.Id, tile with { Neighbors = tile.Neighbors.ToArray() }))
                throw new InvalidDataException("invalid or oversized influence graph");
        }
        foreach (var tile in tiles.Values)
            foreach (int neighbor in tile.Neighbors)
                if (tiles.TryGetValue(neighbor, out var other) && !other.Neighbors.Contains(tile.Id))
                    throw new InvalidDataException("surface adjacency must be symmetric");
    }

    public InfluenceTile Get(int id) => tiles.TryGetValue(id, out var tile) ? tile : throw new InvalidDataException("influence graph is missing a reached tile");
}
