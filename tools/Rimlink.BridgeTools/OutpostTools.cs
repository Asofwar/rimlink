using RimBridgeServer.Sdk;
using RimWorld.Planet;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Outposts;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>Dev companion uses the native production entry points, never changes feature readiness or invents receipts.</summary>
public sealed class OutpostTools
{
    [Tool("rimlink/outposts", Description = "Read outpost custody/UI status without spawning or returning anything")]
    public Task<object> Status(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx, () => new
    {
        success = true, capability = RimlinkSession.Connection?.Login?.OutpostVersion ?? 0,
        busy = OutpostOutgoing.Busy, status = OutpostOutgoing.StatusKey, error = OutpostOutgoing.LastFailure,
        pendingAdmissions = OutpostOutgoing.PendingAdmissionIds, deliveryError = OutpostIncoming.LastFailure, commandError = OutpostSync.LastFailure,
        markers = Find.WorldObjects?.AllWorldObjects.OfType<OutpostWorldObject>().Count() ?? 0,
        outposts = RimlinkSession.State.Outposts.Outposts.Select(o => new { id = o.OutpostId, o.Tile, profile = o.Profile.ToString(), o.Stock, workers = o.PawnCustodyIds.Count, o.Archived }).ToArray(),
        manifest = Find.WindowStack?.Windows.OfType<Dialog_OutpostManifest>().LastOrDefault() is { } dialog
            ? new { busy = dialog.Busy, status = dialog.StatusKey, id = dialog.Staged?.ManifestId, digest = dialog.Staged?.Digest } : null
    });

    [Tool("rimlink/outpost_staff", Description = "Found or staff from an actually arrived own caravan through exact pre/post saves; no shortcuts", ResultDescription = "started; follow rimlink/outposts")]
    public Task<object> Staff(IRimBridgeContext ctx,
        [ToolParameter(Description = "Caravan id or name", Required = true)] string caravan,
        [ToolParameter(Description = "Comma-separated actual caravan pawn ids or unique names", Required = true)] string pawns,
        [ToolParameter(Description = "wood or steel")] string profile = "wood",
        [ToolParameter(Description = "Existing outpost id, otherwise found")] string? outpostId = null) => Bridge.OnMain(ctx, () =>
    {
        if (!RimlinkSession.Active || RimlinkSession.Connection?.Login?.OutpostVersion != 1) return Bridge.Fail("outpost capability unavailable; no feature-gate override");
        if (FindCaravan(caravan) is not { } c) return Bridge.Fail("caravan not found");
        if (!c.Spawned || c.pather.Moving || !c.IsPlayerControlled || OutpostOutgoing.Busy) return Bridge.Fail("own arrived caravan required; operation must be idle");
        OutpostProfile kind = profile.ToLowerInvariant() switch { "wood" => OutpostProfile.Wood, "steel" => OutpostProfile.Steel, _ => OutpostProfile.Unspecified };
        if (kind == OutpostProfile.Unspecified) return Bridge.Fail("profile must be wood or steel");
        var selected = new List<Pawn>();
        foreach (string name in pawns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var matches = c.PawnsListForReading.Where(p => p.GetUniqueLoadID() == name || string.Equals(p.LabelShort, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || selected.Any(p => ReferenceEquals(p, matches[0]))) return Bridge.Fail("each worker must identify exactly one actual caravan pawn");
            selected.Add(matches[0]);
        }
        if (selected.Count is < 1 or > 4) return Bridge.Fail("select 1..4 actual workers");
        return Targets.Act(() => OutpostOutgoing.Begin(c, selected, kind, string.IsNullOrWhiteSpace(outpostId) ? null : outpostId), "exact outpost staffing started");
    });

    [Tool("rimlink/outpost_staffing_preview", Description = "Open the native staffing preview for an actual caravan; does not remove workers or bypass server capability")]
    public Task<object> Preview(IRimBridgeContext ctx, [ToolParameter(Description = "Caravan id or name", Required = true)] string caravan) => Bridge.OnMain(ctx, () =>
    {
        if (FindCaravan(caravan) is not { } c) return Bridge.Fail("caravan not found");
        Find.WindowStack.Add(new Dialog_OutpostStaffing(c)); return Targets.Started("native staffing preview opened");
    });

    [Tool("rimlink/outpost_cancel", Description = "Query and safely cancel the original staffing operation; never restores pawns locally")]
    public Task<object> Cancel(IRimBridgeContext ctx, [ToolParameter(Description = "Original client operation id", Required = true)] string operation) => Bridge.OnMain(ctx, () =>
    {
        if (OutpostOutgoing.Busy || !OutpostOutgoing.PendingAdmissionIds.Contains(operation)) return Bridge.Fail("no idle pending admission with this id");
        return Targets.Act(() => OutpostOutgoing.Cancel(operation), "safe cancellation started");
    });

    [Tool("rimlink/outpost_manifest_preview", Description = "Read actual complete main-planet export fingerprints and geography counts; does not upload, stage or confirm")]
    public Task<object> ManifestPreview(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx, () =>
    {
        var document = OutpostManifestCapture.Capture(Find.World);
        return new { success = true, staged = false, confirmed = false, tiles = document.TileCount,
            complete = document.Tiles.Count == document.TileCount && document.Tiles.Select(t => t.Tile).SequenceEqual(Enumerable.Range(0, document.TileCount)),
            land = document.Tiles.Count(t => t.Land), passable = document.Tiles.Count(t => t.CaravanPassable), wooded = document.Tiles.Count(t => t.NaturalTrees),
            document.GameVersion, dlc = document.ActiveDlc.ToArray(), document.MainSurfaceId,
            paramsSha256 = Convert.ToHexString(document.Binding.WorldParamsSha256.Span), defsSha256 = Convert.ToHexString(document.GameDefsSha256.Span),
            resources = document.Resources.Select(v => new { v.DefName, v.UnitValueX100 }).ToArray() };
    });

    [Tool("rimlink/outpost_manifest", Description = "Admin stage actual geography for review, OR explicitly confirm the exact staged id+digest; never auto-confirms")]
    public Task<object> Manifest(IRimBridgeContext ctx,
        [ToolParameter(Description = "stage or confirm", Required = true)] string action,
        [ToolParameter(Description = "Exact staged manifest id for confirm")] string? manifestId = null,
        [ToolParameter(Description = "Exact staged digest for confirm")] string? digest = null) => Bridge.OnMain(ctx, () =>
    {
        if (!RimlinkSession.Active || !RimlinkSession.IsAdmin) return Bridge.Fail("authenticated administrator required");
        var dialog = Find.WindowStack.Windows.OfType<Dialog_OutpostManifest>().LastOrDefault();
        if (action == "stage")
        {
            if (dialog?.Busy == true) return Bridge.Fail("geography upload already running");
            if (dialog is null) { dialog = new(); Find.WindowStack.Add(dialog); }
            _ = dialog.StageAsync(); return Targets.Started("actual world export/stage started; review before explicit confirm");
        }
        if (action != "confirm" || dialog?.Busy != false || dialog.Staged is not { } preview || preview.ManifestId != manifestId || preview.Digest != digest)
            return Bridge.Fail("confirm requires the exact id and digest visible in an idle staged review");
        _ = dialog.ConfirmAsync(); return Targets.Started("explicit manifest confirmation started");
    });

    [Tool("rimlink/outpost_controls", Description = "Open native owner controls without creating an outpost, map or delivery")]
    public Task<object> Controls(IRimBridgeContext ctx) => Bridge.OnMain(ctx, () =>
    {
        Find.WindowStack.Add(new Rimlink.Client.UI.Outposts.Dialog_Outpost()); return Targets.Started("native owner controls opened");
    });

    [Tool("rimlink/outpost_mutate", Description = "Claim, recall, close or switch through the same fresh exact snapshot and server CAS as native controls")]
    public Task<object> Mutate(IRimBridgeContext ctx,
        [ToolParameter(Description = "Existing owned outpost id", Required = true)] string outpost,
        [ToolParameter(Description = "claim, recall, close, switch", Required = true)] string action,
        [ToolParameter(Description = "Comma-separated exact custody ids for partial recall")] string workers = "",
        [ToolParameter(Description = "wood or steel for switch")] string profile = "wood",
        [ToolParameter(Description = "Explicit confirmation of fractional loss and closure warning")] bool confirmFractionDiscard = false) => Bridge.OnMain(ctx, () =>
    {
        if (!RimlinkSession.Active || RimlinkSession.Login?.OutpostVersion != 1 || OutpostSync.Busy) return Bridge.Fail("current idle outpost session required");
        var row = RimlinkSession.State.Outposts.Outposts.SingleOrDefault(o => o.OutpostId == outpost);
        if (row is null) return Bridge.Fail("owned outpost not found");
        var kind = action switch { "claim" => OutpostMutationKind.Claim, "recall" => OutpostMutationKind.Recall, "close" => OutpostMutationKind.Close,
            "switch" => OutpostMutationKind.Switch, _ => OutpostMutationKind.Unspecified };
        if (kind == OutpostMutationKind.Unspecified) return Bridge.Fail("unknown outpost action");
        string[] ids = workers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (kind == OutpostMutationKind.Recall && (ids.Length == 0 || ids.Distinct().Count() != ids.Length || ids.Any(id => !row.PawnCustodyIds.Contains(id)))) return Bridge.Fail("exact owned worker ids required");
        if ((kind is OutpostMutationKind.Close or OutpostMutationKind.Switch || kind == OutpostMutationKind.Recall && ids.Length == row.PawnCustodyIds.Count)
            && !confirmFractionDiscard) return Bridge.Fail("explicit fraction/closure warning confirmation required");
        var selected = profile switch { "wood" => OutpostProfile.Wood, "steel" => OutpostProfile.Steel, _ => OutpostProfile.Unspecified };
        if (kind == OutpostMutationKind.Switch && selected == OutpostProfile.Unspecified) return Bridge.Fail("profile must be wood or steel");
        OutpostSync.Mutate(row, kind, ids, selected, confirmFractionDiscard); return Targets.Started("exact owner command started");
    });

    [Tool("rimlink/outpost_deliver", Description = "Run the normal mailbox fetch/begin/receipt ingress; no fabricated authorization or local spawn shortcut")]
    public Task<object> Deliver(IRimBridgeContext ctx) => Bridge.OnMain(ctx, () =>
    {
        if (!RimlinkSession.Active || Find.CurrentMap is not { IsPlayerHome: true }) return Bridge.Fail("current own home map required");
        Rimlink.Client.Game.Economy.Incoming.DeliverPending(); return Targets.Started("normal protected delivery ingress requested");
    });

    private static Caravan? FindCaravan(string id) => Find.WorldObjects?.Caravans.FirstOrDefault(c => c.GetUniqueLoadID() == id || string.Equals(c.Name, id, StringComparison.OrdinalIgnoreCase));
}
