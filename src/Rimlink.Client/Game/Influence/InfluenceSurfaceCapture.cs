using System.Collections;
using System.Diagnostics;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Influence;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game.Influence;

public sealed record InfluenceGeometry(Vector3 Center, Vector3[] Vertices, int[] VertexIds);
public readonly record struct InfluenceSurfaceProof(int Tile, bool Root, string WorldId, long Generation);

public sealed class InfluenceSurfaceCapture(InfluenceSnapshot snapshot)
{
    public Dictionary<int, InfluenceGeometry> Geometry { get; } = [];
    public IReadOnlyCollection<InfluenceTile> Tiles => tiles.Values;
    public bool Complete { get; private set; }
    private readonly Dictionary<int, InfluenceTile> tiles = [];

    /// <summary>Main thread only. Each PlanetTile is tied to the actual Surface, never to an assumed layer zero.</summary>
    public IEnumerator Capture()
    {
        if (!InfluenceValidation.IsValid(snapshot)) throw new InvalidDataException("invalid influence snapshot");
        var grid = Find.WorldGrid;
        var surface = grid.Surface;
        var neighbors = new List<PlanetTile>();
        var vertices = new List<Vector3>();
        var vertexIds = new Dictionary<Vector3, int>();
        var budget = Stopwatch.StartNew();
        int portion = 0, visits = 0;
        foreach (var source in snapshot.Sources)
        {
            var seen = new HashSet<int> { source.Tile };
            var queue = new Queue<(int Id, int Distance)>(); queue.Enqueue((source.Tile, 0));
            while (queue.TryDequeue(out var at))
            {
                if (++visits > InfluenceLimits.MaxVisitedTiles || at.Id < 0 || at.Id >= surface.TilesCount) throw new InvalidDataException("influence graph budget or surface range exceeded");
                if (!tiles.TryGetValue(at.Id, out var tile))
                {
                    var native = new PlanetTile(at.Id, surface);
                    neighbors.Clear(); grid.GetTileNeighbors(native, neighbors);
                    if (neighbors.Count > 6 || neighbors.Any(n => n.Layer != surface || n.tileId < 0 || n.tileId >= surface.TilesCount)) throw new InvalidDataException("unsupported surface topology");
                    bool water = grid[native].WaterCovered;
                    tiles.Add(at.Id, tile = new InfluenceTile(at.Id, water, neighbors.Select(n => n.tileId).ToArray()));
                    if (!water)
                    {
                        vertices.Clear(); grid.GetTileVertices(native, vertices);
                        if (vertices.Count is < 3 or > 8) throw new InvalidDataException("unsupported surface polygon");
                        // The engine's packed-list indices are unique per polygon, even for the same physical corner.
                        int[] ids = vertices.Select(v => { if (!vertexIds.TryGetValue(v, out int id)) vertexIds.Add(v, id = vertexIds.Count); return id; }).ToArray();
                        Geometry.Add(at.Id, new InfluenceGeometry(grid.GetTileCenter(native), vertices.ToArray(), ids));
                    }
                }
                if (!tile.Water && at.Distance < source.Radius)
                    foreach (int neighbor in tile.Neighbors)
                        if (seen.Add(neighbor)) queue.Enqueue((neighbor, at.Distance + 1));
                if (++portion >= 256 || budget.Elapsed.TotalMilliseconds >= 2)
                {
                    portion = 0; yield return null; budget.Restart();
                }
            }
        }
        Complete = true;
    }

    public static bool TryOwnColony(int serverTile, out InfluenceSurfaceProof proof)
    {
        proof = default;
        if (!RimlinkSession.Active || Current.ProgramState != ProgramState.Playing || Find.World is null
            || Live.MirrorMode.Active || Visits.VisitSession.InVisit || RimlinkSession.World is not { } world
            || Current.Game.GetComponent<RimlinkGameComponent>() is not { } component) return false;
        var homes = Find.Maps.Where(m => m.IsPlayerHome && m.Parent is Settlement && m.Parent.Faction == Faction.OfPlayer)
            .Select(m => (Settlement)m.Parent).Where(s => s.Tile.tileId == serverTile).Distinct().ToArray();
        if (homes.Length != 1) return false;
        var original = homes[0].Tile;
        proof = new InfluenceSurfaceProof(original.tileId, original.Layer == Find.WorldGrid.Surface && original.Layer.IsRootSurface, world.WorldInstanceId, component.SaveGeneration);
        return true;
    }
}
