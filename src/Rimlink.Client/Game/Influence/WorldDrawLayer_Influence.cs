using System.Collections;
using System.Diagnostics;
using RimWorld.Planet;
using Rimlink.Client.Influence;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game.Influence;

public static class InfluenceColors
{
    public static readonly Color32[] Palette =
    [
        new(80, 160, 220, 255), new(230, 159, 0, 255), new(0, 158, 115, 255), new(204, 121, 167, 255),
        new(213, 94, 0, 255), new(125, 100, 210, 255), new(230, 215, 90, 255), new(70, 195, 190, 255),
        new(190, 85, 115, 255), new(125, 170, 80, 255), new(145, 135, 105, 255), new(155, 120, 195, 255),
        new(75, 125, 165, 255), new(195, 155, 110, 255), new(100, 165, 150, 255), new(180, 160, 205, 255),
    ];
    public static readonly Color32 Disputed = new(220, 205, 155, 255);
}

public readonly record struct InfluenceVertexStyle(Color32 Color, long AllianceId, bool Border);
public sealed record InfluenceBatch(LayerSubMesh Mesh, List<InfluenceVertexStyle> Styles);

public sealed class WorldDrawLayer_Influence : WorldDrawLayer
{
    private List<InfluenceBatch> batches = [];
    public Material Material { get; } = new(WorldMaterials.VertexColorTransparent);
    public long Focus { get; private set; }
    private bool stale;
    public int VertexCount => batches.Sum(b => b.Mesh.verts.Count);
    public int MeshCount => batches.Count;
    public override bool Visible => base.Visible && InfluenceLayerController.Visible && RimlinkSession.Active;
    public override bool VisibleWhenLayerNotSelected => false;
    public override bool VisibleInBackground => false;

    // The engine's base implementation clears the displayed meshes; candidates are built separately instead.
    public override IEnumerable Regenerate() { dirty = false; yield break; }

    public void Replace(List<InfluenceBatch> next)
    {
        var old = batches;
        subMeshes.Clear(); subMeshes.AddRange(next.Select(b => b.Mesh)); batches = next;
        Destroy(old);
        ApplyStyle(Focus, stale, force: true);
    }

    public void ApplyStyle(long focus, bool isStale, bool force = false)
    {
        if (!force && focus == Focus && isStale == stale) return;
        Focus = focus; stale = isStale;
        foreach (var batch in batches)
        {
            var colors = new Color32[batch.Styles.Count];
            for (int i = 0; i < colors.Length; i++)
            {
                var style = batch.Styles[i]; var color = style.Color;
                if (focus > 0 && style.AllianceId > 0 && style.AllianceId != focus) color.a = (byte)(color.a / 3);
                if (isStale) color.a = (byte)(color.a / 2);
                if (style.Border && style.AllianceId == focus && focus > 0) color = new Color32(255, 255, 245, isStale ? (byte)125 : (byte)250);
                colors[i] = color;
            }
            batch.Mesh.mesh.colors32 = colors;
        }
    }

    public void Release()
    {
        Dispose(); batches.Clear(); subMeshes.Clear();
        UnityEngine.Object.Destroy(Material);
    }

    public static void Destroy(IEnumerable<InfluenceBatch> batches)
    {
        foreach (var batch in batches) UnityEngine.Object.Destroy(batch.Mesh.mesh);
    }
}

public sealed class InfluenceMeshBuilder(InfluenceSnapshot snapshot, InfluenceMap map, IReadOnlyDictionary<int, InfluenceGeometry> geometry, WorldDrawLayer_Influence layer)
{
    private sealed record Edge(Vector3 A, Vector3 B, Vector3 Center, long[] Groups);
    public List<InfluenceBatch> Batches { get; } = [];
    private readonly Dictionary<(int, int), List<Edge>> edges = [];
    private int vertices;
    private InfluenceBatch? current;
    private readonly Dictionary<long, Color32> colors = snapshot.Alliances.ToDictionary(a => a.Id, a => InfluenceColors.Palette[a.PaletteIndex]);

    public IEnumerator Build()
    {
        var budget = Stopwatch.StartNew(); int portion = 0;
        foreach (var (tile, cell) in map.Cells)
        {
            var shape = geometry[tile];
            var groups = cell.Contenders.Select(c => c.AllianceId).ToArray();
            long group = cell.Disputed ? -1 : groups[0];
            var fill = cell.Disputed ? InfluenceColors.Disputed : colors[group]; fill.a = 42;
            for (int i = 0; i < shape.Vertices.Length; i++)
            {
                int j = (i + 1) % shape.Vertices.Length;
                Triangle(shape.Center * 1.001f, shape.Vertices[i] * 1.001f, shape.Vertices[j] * 1.001f, fill, group, false);
                int a = shape.VertexIds[i], b = shape.VertexIds[j]; var key = (Math.Min(a, b), Math.Max(a, b));
                if (!edges.TryGetValue(key, out var owners)) edges[key] = owners = [];
                if (owners.Count >= 2) throw new InvalidDataException("non-manifold influence polygon edge");
                owners.Add(new Edge(shape.Vertices[i], shape.Vertices[j], shape.Center, groups));
            }
            if (cell.Disputed) Hatch(shape);
            if (++portion >= 256 || budget.Elapsed.TotalMilliseconds >= 2) { portion = 0; yield return null; budget.Restart(); }
        }
        foreach (var owners in edges.Values)
        {
            if (owners.Count == 2 && owners[0].Groups.SequenceEqual(owners[1].Groups)) continue;
            foreach (var edge in owners)
            {
                long group = edge.Groups.Length == 1 ? edge.Groups[0] : -1;
                var color = group > 0 ? colors[group] : InfluenceColors.Disputed; color.a = 225;
                float width = Find.WorldGrid.AverageTileSize * (group < 0 ? 0.055f : 0.035f);
                var toward = (edge.Center - (edge.A + edge.B) * 0.5f).normalized * width;
                Quad(edge.A * 1.0015f, edge.B * 1.0015f, (edge.B + toward) * 1.0015f, (edge.A + toward) * 1.0015f, color, group, true);
            }
            if (++portion >= 256 || budget.Elapsed.TotalMilliseconds >= 2) { portion = 0; yield return null; budget.Restart(); }
        }
        edges.Clear();
        foreach (var batch in Batches)
        {
            batch.Mesh.FinalizeMesh(MeshParts.Verts | MeshParts.Tris | MeshParts.Colors);
            batch.Mesh.mesh.RecalculateBounds();
            yield return null;
        }
    }

    private void Hatch(InfluenceGeometry shape)
    {
        var normal = shape.Center.normalized;
        var u = Vector3.Cross(normal, Vector3.up);
        if (u.sqrMagnitude < 0.01f) u = Vector3.Cross(normal, Vector3.right);
        u.Normalize(); var v = Vector3.Cross(normal, u).normalized;
        float span = shape.Vertices.Max(p => (p - shape.Center).magnitude);
        var color = new Color32(45, 45, 40, 145);
        foreach (float offset in new[] { -span * 0.55f, 0f, span * 0.55f })
        {
            var hits = new List<Vector3>();
            for (int i = 0; i < shape.Vertices.Length; i++)
            {
                var a = shape.Vertices[i]; var b = shape.Vertices[(i + 1) % shape.Vertices.Length];
                float fa = Vector3.Dot(a - shape.Center, v - u) - offset, fb = Vector3.Dot(b - shape.Center, v - u) - offset;
                if (Math.Abs(fa - fb) > 0.000001f && (fa <= 0 && fb >= 0 || fa >= 0 && fb <= 0)) hits.Add(Vector3.Lerp(a, b, fa / (fa - fb)));
            }
            if (hits.Count < 2) continue;
            var left = hits[0]; var right = hits.OrderByDescending(p => (p - left).sqrMagnitude).First();
            var side = Vector3.Cross(normal, right - left).normalized * (span * 0.018f);
            Quad((left - side) * 1.0013f, (right - side) * 1.0013f, (right + side) * 1.0013f, (left + side) * 1.0013f, color, -1, false);
        }
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 color, long group, bool border)
    { Triangle(a, b, c, color, group, border); Triangle(a, c, d, color, group, border); }

    private void Triangle(Vector3 a, Vector3 b, Vector3 c, Color32 color, long group, bool border)
    {
        if (((long)vertices + layer.VertexCount + 3) * 128 > 128L * 1024 * 1024) throw new InvalidDataException("influence mesh memory budget exceeded");
        if (current is null || current.Mesh.verts.Count + 3 > 4000)
        {
            current = new InfluenceBatch(new LayerSubMesh(new Mesh { name = "Rimlink influence" }, layer.Material), []);
            Batches.Add(current);
        }
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), a) < 0) (b, c) = (c, b);
        int index = current.Mesh.verts.Count;
        current.Mesh.verts.Add(a); current.Mesh.verts.Add(b); current.Mesh.verts.Add(c);
        for (int i = 0; i < 3; i++)
        {
            current.Mesh.tris.Add(index + i); current.Mesh.colors.Add(color); current.Styles.Add(new InfluenceVertexStyle(color, group, border));
        }
        vertices += 3;
    }
}
