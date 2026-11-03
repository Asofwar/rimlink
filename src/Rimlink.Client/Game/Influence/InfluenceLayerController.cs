using System.Collections;
using System.Diagnostics;
using RimWorld;
using Rimlink.Client.Influence;
using Rimlink.Client.UI;
using Verse;

namespace Rimlink.Client.Game.Influence;

/// <summary>Owns just this feature's layer and resources. Every entry point runs on the main thread.</summary>
public static class InfluenceLayerController
{
    private static SurfaceLayer? surface;
    private static object? world;
    private static WorldDrawLayer_Influence? layer;
    private static InfluenceSurfaceCapture? capture;
    private static InfluenceMeshBuilder? builder;
    private static IEnumerator? work;
    private static InfluenceToken? token;
    private static InfluenceMap? pendingMap;
    private static bool computing, ready;
    private static int generation;
    private static readonly Stopwatch buildTime = new();
    public static InfluenceMap? Map { get; private set; }
    public static double BuildMilliseconds { get; private set; }
    public static double MaxSliceMilliseconds { get; private set; }
    public static int DiscardedCandidates { get; private set; }
    public static bool HoldBeforeSwap { get; set; }
    public static int VertexCount => layer?.VertexCount ?? 0;
    public static int MeshCount => layer?.MeshCount ?? 0;
    public static long Focus => layer?.Focus ?? 0;
    public static bool Enabled => RimlinkSession.Endpoint is { } endpoint && !RimlinkMod.Settings.HiddenInfluenceServers.Contains(endpoint.Id);
    public static bool Visible => Enabled && RimlinkSession.State.Influence.Current is not null && !RimlinkSession.State.Influence.Unavailable;

    public static void SetEnabled(bool enabled)
    {
        if (RimlinkSession.Endpoint is not { } endpoint) return;
        RimlinkMod.Settings.HiddenInfluenceServers.RemoveAll(id => id == endpoint.Id);
        if (!enabled) RimlinkMod.Settings.HiddenInfluenceServers.Add(endpoint.Id);
        LoadedModManager.GetMod<RimlinkMod>().WriteSettings();
        RimlinkSession.State.Influence.Invalidate();
        if (!enabled) CancelWork();
    }

    public static void FocusAlliance(long id) => layer?.ApplyStyle(id == Focus ? 0 : id, RimlinkSession.State.Influence.Stale);

    public static void Tick()
    {
        if (!RimlinkSession.Active || Find.World is null) { Reset(); return; }
        var cache = RimlinkSession.State.Influence;
        if (!ReferenceEquals(world, Find.World) || !ReferenceEquals(surface, Find.WorldGrid.Surface))
        {
            cache.Invalidate();
            Reset(); world = Find.World; surface = Find.WorldGrid.Surface;
            layer = new WorldDrawLayer_Influence(); layer.Initialize(surface); surface.WorldDrawLayers.Add(layer);
        }
        layer!.ApplyStyle(layer.Focus, cache.Stale);
        if (!Enabled) { CancelWork(); return; }
        if (token is { } current && !cache.CanPublish(current)) { DiscardedCandidates++; CancelWork(); }
        if (token is null && cache.CandidateToken is { } next && cache.Candidate is { } snapshot)
        {
            token = next; capture = new InfluenceSurfaceCapture(snapshot); work = capture.Capture(); buildTime.Restart(); MaxSliceMilliseconds = 0;
        }
        try
        {
            if (ready)
            {
                if (HoldBeforeSwap) return;
                if (token is { } completed && cache.CanPublish(completed) && builder is not null)
                {
                    layer.Replace(builder.Batches);
                    builder = null;
                    if (!cache.TryPublish(completed)) throw new InvalidOperationException("influence publication changed on the main thread");
                    Map = pendingMap; BuildMilliseconds = buildTime.Elapsed.TotalMilliseconds;
                }
                CancelWork();
                return;
            }
            if (work is null || computing) return;
            long sliceStarted = Stopwatch.GetTimestamp();
            bool more = work.MoveNext();
            MaxSliceMilliseconds = Math.Max(MaxSliceMilliseconds, Stopwatch.GetElapsedTime(sliceStarted).TotalMilliseconds);
            if (more) return;
            (work as IDisposable)?.Dispose(); work = null;
            if (builder is not null) { ready = true; return; }
            if (capture is not { Complete: true } captured || token is not { } forCalc || cache.Candidate is not { } input) return;
            int version = generation;
            computing = true;
            _ = Task.Run(() =>
            {
                try
                {
                    var result = InfluenceCalculator.Calculate(input, new InfluenceGraph(captured.Tiles));
                    MainThread.Post(() =>
                    {
                        if (version != generation) return;
                        computing = false;
                        if (!cache.CanPublish(forCalc) || capture is null || layer is null) { CancelWork(); return; }
                        pendingMap = result;
                        builder = new InfluenceMeshBuilder(input, result, captured.Geometry, layer);
                        work = builder.Build();
                    });
                }
                catch (Exception ex) { MainThread.Post(() => { if (version == generation) Fail(ex); }); }
            });
        }
        catch (Exception ex) { Fail(ex); }
    }

    private static void Fail(Exception error)
    {
        RimlinkLog.Instance.Warn("influence rendering: " + error.Message);
        CancelWork(); RimlinkSession.State.Influence.MarkUnavailable();
        Map = null; layer?.Replace([]);
    }

    private static void CancelWork()
    {
        generation++;
        (work as IDisposable)?.Dispose(); work = null;
        if (builder is not null) WorldDrawLayer_Influence.Destroy(builder.Batches);
        builder = null; capture = null; token = null; pendingMap = null; computing = false; ready = false;
    }

    public static void Reset()
    {
        CancelWork();
        if (layer is not null) { surface?.WorldDrawLayers.Remove(layer); layer.Release(); }
        layer = null; surface = null; world = null; Map = null;
        HoldBeforeSwap = false;
    }
}
