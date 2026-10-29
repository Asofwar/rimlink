using RimBridgeServer.Sdk;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Live;
using Rimlink.Client.Game.Raids;
using Rimlink.Client.Game.Visits;
using Rimlink.Client.UI.Live;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>
/// Raids, visits and live sessions without the caravan trip or the dialogs: the same entry points the dev debug actions use.
/// Each starts the flow and returns; follow it with rimlink/status and the [Rimlink] log lines.
/// </summary>
public sealed class PlayTools
{
    [Tool("rimlink/raid", Description = "Raid another player with our pawns on the current map (no caravan)",
        ResultDescription = "success/started; the outcome follows in rimlink/status (raids) and the log")]
    public Task<object> Raid(IRimBridgeContext ctx,
        [ToolParameter(Description = "Target player name or id", Required = true)] string player,
        [ToolParameter(Description = "Comma-separated pawn ids (rimworld/list_colonists) or names", Required = true)] string pawns,
        [ToolParameter(Description = "snapshot (copies) or real (the pawns leave)")] string mode = "snapshot",
        [ToolParameter(Description = "instant or warned")] string timing = "instant") => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (mode is not ("snapshot" or "real")) return Bridge.Fail("mode must be snapshot or real");
        if (timing is not ("instant" or "warned")) return Bridge.Fail("timing must be instant or warned");
        if (Targets.Player(player) is not { } target) return Bridge.Fail($"unknown player '{player}'");
        if (Targets.Pawns(pawns, out string? error) is not { } chosen) return Bridge.Fail(error!);
        RaidOutgoing.LaunchFromCaravanless(Find.CurrentMap, target, chosen,
            mode == "real" ? PawnMode.PawnReal : PawnMode.PawnSnapshot,
            timing == "warned" ? RaidTiming.TimingWarned : RaidTiming.TimingInstant);
        return Targets.Started($"{mode} raid on {target.Name} launched", new { pawns = chosen.Select(p => p.LabelShort).ToList() });
    });

    [Tool("rimlink/visit", Description = "Visit another player's colony (a copy of their save) with our pawns on the current map",
        ResultDescription = "success/started; the visit loads the host's save")]
    public Task<object> Visit(IRimBridgeContext ctx,
        [ToolParameter(Description = "Host player name or id", Required = true)] string player,
        [ToolParameter(Description = "Comma-separated pawn ids or names", Required = true)] string pawns) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (VisitSession.InVisit) return Bridge.Fail("a visit is already running");
        if (Targets.Player(player) is not { } host) return Bridge.Fail($"unknown player '{player}'");
        if (Targets.Pawns(pawns, out string? error) is not { } chosen) return Bridge.Fail(error!);
        VisitSession.Begin(host, chosen);
        return Targets.Started($"visit to {host.Name} started", new { pawns = chosen.Select(p => p.LabelShort).ToList() });
    });

    [Tool("rimlink/live_propose", Description = "Propose a live raid or visit to an online player with our pawns on the current map",
        ResultDescription = "success/started; the host answers (rimlink/live_answer on their side)")]
    public Task<object> LivePropose(IRimBridgeContext ctx,
        [ToolParameter(Description = "Host player name or id", Required = true)] string player,
        [ToolParameter(Description = "Comma-separated pawn ids or names", Required = true)] string pawns,
        [ToolParameter(Description = "raid or visit")] string kind = "raid") => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (kind is not ("raid" or "visit")) return Bridge.Fail("kind must be raid or visit");
        if (Targets.Player(player) is not { } host) return Bridge.Fail($"unknown player '{player}'");
        if (Targets.Pawns(pawns, out string? error) is not { } chosen) return Bridge.Fail(error!);
        LiveGuest.ProposeFromMap(Find.CurrentMap, host, kind == "visit" ? LiveKind.LiveVisit : LiveKind.LiveRaid, chosen, RaidTiming.TimingInstant);
        return Targets.Started($"live {kind} proposed to {host.Name}", new { pawns = chosen.Select(p => p.LabelShort).ToList() });
    });

    [Tool("rimlink/live_answer", Description = "Accept or decline the open live-session invitation (host side)",
        ResultDescription = "success/started")]
    public Task<object> LiveAnswer(IRimBridgeContext ctx,
        [ToolParameter(Description = "true to accept, false to decline", Required = true)] bool accept) => Bridge.OnMain(ctx, () =>
    {
        if (!RimlinkSession.Active) return Bridge.Fail("no Rimlink session");
        if (Find.WindowStack.WindowOfType<Dialog_LiveInvite>() is not { } invite)
            return Bridge.Fail("no live invite is open", new { pendingInvite = RimlinkSession.State.Live.PendingInvite?.Session.Id });
        invite.Answer(accept);
        return Targets.Started(accept ? "invitation accepted" : "invitation declined");
    });

    [Tool("rimlink/live_end", Description = "End the live session: the guest ends it, the host aborts it (no confirmation dialog)",
        ResultDescription = "success/started")]
    public Task<object> LiveEnd(IRimBridgeContext ctx) => Bridge.OnMain(ctx, () =>
    {
        if (!RimlinkSession.Active) return Bridge.Fail("no Rimlink session");
        if (LiveHost.Active) { LiveHost.Abort(); return Targets.Started("live session aborted by the host"); }
        if (LiveGuest.Session is not null) { LiveGuest.EndByPlayer(); return Targets.Started("live session ended by the guest"); }
        return Bridge.Fail("no live session");
    });
}
