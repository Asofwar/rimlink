using RimBridgeServer.Sdk;
using System.Diagnostics;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Influence;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>Dev-only readouts and the same opt-in flow as the influence dialog.</summary>
public sealed class InfluenceTools
{
    [Tool("rimlink/influence_profile", Description = "Dev-only frame interval sample in the actual game; use identical camera for overlay on/off comparisons")]
    public async Task<object> Profile(IRimBridgeContext ctx, [ToolParameter(Description = "Sample duration, 1..40 seconds")] int seconds = 30)
    {
        seconds = Math.Clamp(seconds, 1, 40);
        var clock = Stopwatch.StartNew(); var samples = new List<double>(); long previous = Stopwatch.GetTimestamp();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            await ctx.Game.NextFrameAsync();
            long now = Stopwatch.GetTimestamp(); samples.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds); previous = now;
        }
        samples.Sort();
        if (samples.Count == 0) return Bridge.Fail("no frames");
        return new { success = true, frames = samples.Count, p50 = samples[samples.Count / 2], p95 = samples[(int)((samples.Count - 1) * .95)],
            max = samples[^1], managedBytes = GC.GetTotalMemory(false), workingSetBytes = Process.GetCurrentProcess().WorkingSet64 };
    }

    [Tool("rimlink/influence_sites", Description = "Read up to 128 widely spaced land tile ids from the actual root surface for an isolated cap fixture")]
    public Task<object> Sites(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx, () =>
    {
        if (Find.World is null) return Bridge.Fail("no world");
        var surface = Find.WorldGrid.Surface; var result = new List<int>();
        for (int id = 0; id < surface.TilesCount && result.Count < 128; id += 353)
            if (!Find.WorldGrid[new PlanetTile(id, surface)].WaterCovered) result.Add(id);
        return new { success = result.Count == 128, tiles = result };
    });

    [Tool("rimlink/influence_status", Description = "Influence cache, rendered cells, geometry counts and rebuild time; dev acceptance only")]
    public Task<object> Status(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx, () =>
    {
        var cache = RimlinkSession.State.Influence;
        return new { success = true, cache.WorldId, cache.ProcessId, cache.HighWater, cache.Stale, cache.Unavailable,
            current = cache.Current?.Revision, candidate = cache.Candidate?.Revision,
            snapshotBytes = cache.Current?.CalculateSize(),
            sources = cache.Current?.Sources.Select(s => new { s.Id, s.Tile, s.AllianceId }).ToArray(),
            InfluenceLayerController.Enabled, InfluenceLayerController.Visible, InfluenceLayerController.BuildMilliseconds,
            InfluenceLayerController.MaxSliceMilliseconds,
            InfluenceLayerController.VertexCount, InfluenceLayerController.MeshCount, InfluenceLayerController.DiscardedCandidates,
            cells = InfluenceLayerController.Map?.Cells.Select(c => new { tile = c.Key, c.Value.Disputed, claims = c.Value.Contenders }).ToArray(),
            inspect = InfluenceOverlay.Inspect() };
    });

    [Tool("rimlink/influence", Description = "Dev acceptance: publish/disable own colony, color, toggle, hold/release mesh swap, or open settings. Publication uses the real loaded colony surface and outbox")]
    public async Task<object> Change(IRimBridgeContext ctx,
        [ToolParameter(Required = true)] string action,
        [ToolParameter(Description = "Palette index for color, 0..15")] int value = 0)
    {
        if (action is "settings" or "toggle" or "hold" or "release")
            return await Bridge.OnMain<object>(ctx, () =>
            {
                if (action == "settings") InfluenceOverlay.OpenSettings();
                else if (action == "toggle") InfluenceLayerController.SetEnabled(value != 0);
                else InfluenceLayerController.HoldBeforeSwap = action == "hold";
                return new { success = true };
            });
        var api = RimlinkSession.Influence;
        var outbox = RimlinkSession.Outbox;
        if (api is null || outbox is null) return Bridge.Fail("no session");
        try
        {
            if (action == "color")
            {
                var community = await RimlinkSession.Community!.Get(CancellationToken.None);
                if (community.Alliance is not { } alliance) return Bridge.Fail("no alliance");
                await api.Submit(new InfluenceMutation { Color = new InfluenceColor { AllianceId = alliance.Id,
                    WorldInstanceId = RimlinkSession.World!.WorldInstanceId, PaletteIndex = value } }, outbox, CancellationToken.None);
            }
            else if (action is "publish" or "disable")
            {
                var home = await Bridge.OnMain(ctx, () => RimlinkSession.State.Objects.All.FirstOrDefault(o => o.Type == "settlement" && o.Owner.PlayerId == RimlinkSession.MyPlayerId));
                if (home is null) return Bridge.Fail("no colony");
                var state = await api.GetSetting(new InfluenceTarget { SettlementId = home.Id }, CancellationToken.None);
                long version = state.Version;
                if (action == "publish")
                {
                    var proof = await Bridge.OnMain(ctx, () => InfluenceSurfaceCapture.TryOwnColony(state.Tile, out var p) ? p : (InfluenceSurfaceProof?)null);
                    if (proof is not { Root: true } p || p.WorldId != state.Context.WorldInstanceId || p.Generation != state.Context.SaveGeneration) return Bridge.Fail("loaded colony does not match context");
                    version = (await api.Submit(new InfluenceMutation { Surface = new InfluenceSurface { Context = state.Context,
                        ExpectedVersion = version, Tile = p.Tile, RootSurface = p.Root } }, outbox, CancellationToken.None)).AppliedVersion;
                }
                await api.Submit(new InfluenceMutation { Publication = new InfluencePublication { Context = state.Context,
                    ExpectedVersion = version, Enabled = action == "publish" } }, outbox, CancellationToken.None);
            }
            else return Bridge.Fail("unknown action");
            return new { success = true };
        }
        catch (Exception ex) { return Bridge.Fail(ex.Message); }
    }

    [Tool("rimlink/alliance_create", Description = "Create an alliance through the usual community API and persisted outbox")]
    public async Task<object> Create(IRimBridgeContext ctx, [ToolParameter(Required = true)] string name)
    {
        if (RimlinkSession.Community is not { } api || RimlinkSession.Outbox is not { } outbox) return Bridge.Fail("no session");
        try { await api.Submit(new CommunityRequest { Action = CommunityAction.AllianceCreate, Text = name }, outbox, CancellationToken.None); return new { success = true }; }
        catch (Exception ex) { return Bridge.Fail(ex.Message); }
    }

    [Tool("rimlink/influence_tiles", Description = "Dev readout: root surface land tiles at up to 3 neighbor steps from a tile, for isolated influence fixtures")]
    public Task<object> Tiles(IRimBridgeContext ctx, [ToolParameter(Required = true)] int tile) => Bridge.OnMain<object>(ctx, () =>
    {
        if (Find.World is null || tile < 0 || tile >= Find.WorldGrid.Surface.TilesCount) return Bridge.Fail("invalid tile");
        var seen = new HashSet<int> { tile }; var queue = new Queue<(int Id, int Distance)>(); queue.Enqueue((tile, 0));
        var result = new List<object>(); var neighbors = new List<PlanetTile>();
        while (queue.TryDequeue(out var at))
        {
            var native = new PlanetTile(at.Id, Find.WorldGrid.Surface); bool water = Find.WorldGrid[native].WaterCovered;
            result.Add(new { tile = at.Id, distance = at.Distance, water, canSettle = TileFinder.IsValidTileForNewSettlement(native) });
            if (at.Distance >= 3 || water) continue;
            neighbors.Clear(); Find.WorldGrid.GetTileNeighbors(native, neighbors);
            foreach (var n in neighbors) if (seen.Add(n.tileId)) queue.Enqueue((n.tileId, at.Distance + 1));
        }
        return new { success = true, tiles = result };
    });
}
