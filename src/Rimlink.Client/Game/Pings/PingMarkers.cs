using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Rimlink.Client.Game.Pings;

/// <summary>
/// Ping marks on the map and the world map (live UX spec §2). They last 8 seconds of real time: vanilla flecks and the world debug
/// drawer count game ticks, which stand still on pause and in the live mirror.
/// </summary>
public static class PingMarkers
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    private sealed record Mark(int Tile, IntVec3? Cell, DateTime Until);

    private static readonly List<Mark> marks = [];

    /// <summary>A mark plus, for a received ping, the message with a link to the place and the bell. Main thread only.</summary>
    public static void Show(int tile, IntVec3? cell, string fromName, bool received)
    {
        if (Current.ProgramState != ProgramState.Playing || Find.World is null) return;
        var map = Find.Maps.FirstOrDefault(m => m.Tile == tile);
        if (cell is { } c && (map is null || !c.InBounds(map))) cell = null;
        marks.RemoveAll(m => m.Until <= DateTime.UtcNow);
        marks.Add(new Mark(tile, cell, DateTime.UtcNow + Lifetime));
        if (!received) return;
        bool onWorld = tile >= 0 && tile < Find.WorldGrid.TilesCount;
        var look = cell is { } at && map is not null ? new LookTargets(at, map) : onWorld ? new LookTargets(new GlobalTargetInfo((PlanetTile)tile)) : LookTargets.Invalid;
        Messages.Message("Rimlink.Ping.Received".Translate(fromName), look, MessageTypeDefOf.NeutralEvent, historical: false);
        SoundDefOf.TinyBell.PlayOneShotOnCamera();
    }

    /// <summary>Pulse between 0.6 and 1.4 times the base size, a little over once a second.</summary>
    private static float Pulse => 1f + 0.4f * Mathf.Sin(Time.realtimeSinceStartup * 7f);

    public static void DrawOnMap()
    {
        if (marks.Count == 0 || Find.CurrentMap is not { } map) return;
        foreach (var m in marks)
            if (m.Until > DateTime.UtcNow && m.Cell is { } cell && m.Tile == map.Tile) GenDraw.DrawCircleOutline(cell.ToVector3Shifted(), 1.5f * Pulse, SimpleColor.Yellow);
    }

    public static void DrawOnWorld()
    {
        if (marks.Count == 0 || !WorldRendererUtility.WorldSelected || Find.WorldGrid is not { } grid) return;
        foreach (var m in marks)
            if (m.Until > DateTime.UtcNow && m.Tile >= 0 && m.Tile < grid.TilesCount)
                WorldRendererUtility.DrawQuadTangentialToPlanet(grid.GetTileCenter(m.Tile), grid.AverageTileSize * 1.6f * Pulse, 0.015f, WorldMaterials.SelectedTile);
    }

    public static void Clear() => marks.Clear();
}
