using System.Diagnostics;
using Grpc.Core;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Net;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

internal sealed class BattleRunningViewer(BattleViewerMarkerV1 marker, BattlePreparedManifestV1 manifest, BattleFighterSide side, Map map, bool startCommitted)
{
    private readonly Dictionary<string, Pawn> pawns = manifest.Entities.ToDictionary(e => e.FighterId,
        e => map.mapPawns.AllPawnsSpawned.Single(p => p.ThingID == e.EntityId));
    private readonly List<IntVec3> zone = (from x in Enumerable.Range(manifest.Arena.ZoneMinX, manifest.Arena.ZoneMaxX - manifest.Arena.ZoneMinX + 1)
        from z in Enumerable.Range(manifest.Arena.ZoneMinZ, manifest.Arena.ZoneMaxZ - manifest.Arena.ZoneMinZ + 1)
        select new IntVec3(x, 0, z)).ToList();
    private readonly BattleRunningViewBuffer views = new(manifest);
    private BattleRunningViewV1? display;
    private BattleAppliedReceipt? result;
    private CancellationTokenSource? stop;
    private Task? loop;
    private BattleCommanderLink? commands;
    private bool sending;
    private bool readyAccepted = startCommitted;
    private long priorChannel;
    internal bool Running => display is not null || result is not null;
    internal BattleRunningViewV1? ReadView() => OnBattleMap ? display?.Clone() : null;
    private bool aborted;
    internal BattleAppliedReceipt? ReadResult() => OnBattleMap ? result?.Clone() : null;
    private bool OnBattleMap => Current.ProgramState == ProgramState.Playing && BattleGameContext.IsBattleGame && BattleGameContext.Role == BattleMapRole.Viewer
        && marker.Battle.Equals(BattleGameContext.Scope) && Find.CurrentMap == map;

    internal void Begin(bool attackerReady)
    {
        if (!OnBattleMap || BattlePreparedViewer.Busy || result is not null || loop is { IsCompleted: false } || attackerReady && side != BattleFighterSide.Attacker)
            throw Invalid();
        stop?.Dispose(); stop = new();
        var token = stop.Token;
        loop = Task.Run(() => Run(attackerReady && !readyAccepted, token));
    }

    internal void Stop()
    {
        stop?.Cancel();
        commands = null;
    }

    private async Task Run(bool ready, CancellationToken ct)
    {
        BattleCommanderLink? link = null;
        bool full = true;
        long started = Stopwatch.GetTimestamp();
        try
        {
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(25))
            {
                var connection = await MainThread.Get(() => OnBattleMap && !BattlePreparedViewer.Busy ? RimlinkSession.Connection : null);
                ct.ThrowIfCancellationRequested();
                if (connection is null) return;
                if (connection.State != ConnectionState.Connected)
                {
                    await ClearCommands();
                    await Task.Delay(750, ct);
                    continue;
                }
                try
                {
                    if (link?.Current != true)
                    {
                        await ClearCommands();
                        link = new BattleCommanderLink(connection, marker, manifest, side, startCommitted);
                        full = true;
                    }
                    if (ready)
                    {
                        await link.Ready(ct);
                        ready = false;
                        await MainThread.Get(() => { readyAccepted = true; return true; });
                    }
                    if (link.ChannelEpoch == 0)
                    {
                        await link.Attach(priorChannel, ct);
                        priorChannel = link.ChannelEpoch;
                    }
                    long after = full ? 0 : await MainThread.Get(() => OnBattleMap && !views.NeedsSnapshot ? display?.Sequence ?? 0 : 0);
                    var bytes = await link.Read(after, ct);
                    await MainThread.Get(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!OnBattleMap || BattlePreparedViewer.Busy || !link.Current) return false;
                        if (!bytes.IsEmpty && views.Apply(bytes)) ApplyView(views.Current!);
                        commands = link;
                        return true;
                    });
                    if (!bytes.IsEmpty) full = false;
                    await Task.Delay(250, ct);
                }
                catch (RpcException error) when (error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded
                    or StatusCode.FailedPrecondition or StatusCode.ResourceExhausted)
                {
                    await ClearCommands();
                    if (error.StatusCode == StatusCode.FailedPrecondition)
                    {
                        if (link is { Current: true } && await ReadResult(link, ct)) return;
                        if (link?.ChannelEpoch > 0) link = null;
                    }
                    await Task.Delay(1500, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && link?.Current != true)
                {
                    await ClearCommands(); link = null;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { RimlinkLog.Instance.Warn("battle viewer control held: " + error.GetType().Name); }
        finally { await ClearCommands(); }
    }

    private Task<bool> ClearCommands() => MainThread.Get(() => { commands = null; return true; });

    private async Task<bool> ReadResult(BattleCommanderLink link, CancellationToken ct)
    {
        BattleCompletedViewReply? completed;
        try { completed = await link.ReadResult(ct); }
        catch (RpcException error) when (error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded
            or StatusCode.FailedPrecondition or StatusCode.ResourceExhausted) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !link.Current) { return false; }
        if (completed is null) return false;
        var terminal = BattleManifestContracts.ReadCompletedView(completed, manifest);
        return await MainThread.Get(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!OnBattleMap || BattlePreparedViewer.Busy || !link.Current) return false;
            if (completed.Abort is not null) { aborted = true; return true; }
            if (terminal is null) throw Invalid();
            if (display is { } frame && (terminal.Sequence <= frame.Sequence || terminal.TerminalTick < frame.Tick)) throw Invalid();
            result = completed.Receipt.Clone();
            return true;
        });
    }

    private void ApplyView(BattleRunningViewV1 frame)
    {
        if (!OnBattleMap) throw Invalid();
        foreach (var row in frame.Pawns)
        {
            var pawn = pawns[row.Pawn.FighterId];
            if (row.Pawn.EntityId != pawn.ThingID || pawn.Spawned && pawn.Map != map) throw Invalid();
            if (!row.Spawned || !row.Pawn.Alive)
            {
                if (pawn.Spawned) pawn.DeSpawn();
                continue;
            }
            var cell = new IntVec3(row.Pawn.X, 0, row.Pawn.Z);
            if (!pawn.Spawned) GenSpawn.Spawn(pawn, cell, map, new Rot4(row.Rotation));
            else
            {
                if (pawn.Position != cell) pawn.Position = cell;
                pawn.Rotation = new Rot4(row.Rotation);
            }
        }
        display = frame;
    }

    internal Task<BattleSubmitCommandReply> Order(BattleCommandV1 command)
    {
        if (!OnBattleMap || BattlePreparedViewer.Busy || aborted || sending || display is null || commands is not { Current: true } link
            || stop is null || stop.IsCancellationRequested) throw Invalid();
        sending = true;
        return Send(link, command.Clone(), stop.Token);
    }

    private async Task<BattleSubmitCommandReply> Send(BattleCommanderLink link, BattleCommandV1 command, CancellationToken ct)
    {
        try { return await link.Send(command, ct); }
        finally { await MainThread.Get(() => { sending = false; return true; }); }
    }

    internal void Draw(float x)
    {
        if (!OnBattleMap || BattlePreparedViewer.Busy) return;
        if (aborted)
        {
            Widgets.Label(new Rect(x, 180, 240, 52), "Rimlink.GuildBattle.OperatorAbort".Translate());
            return;
        }
        if (result is { } completed)
        {
            string outcome = completed.Outcome switch
            {
                BattleTerminalOutcome.AttackerWin => "Rimlink.GuildBattle.AttackerWin",
                BattleTerminalOutcome.DefenderWin => "Rimlink.GuildBattle.DefenderWin",
                BattleTerminalOutcome.Draw => "Rimlink.GuildBattle.Draw",
                _ => "Rimlink.GuildBattle.TechnicalAbort"
            };
            Widgets.Label(new Rect(x, 180, 240, 52), outcome.Translate());
            return;
        }
        if (loop is null || loop.IsCompleted)
        {
            string key = side == BattleFighterSide.Attacker && !readyAccepted ? "Rimlink.GuildBattle.Ready" : "Rimlink.GuildBattle.JoinControl";
            if (Widgets.ButtonText(new Rect(x, 180, 240, 28), key.Translate())) Begin(side == BattleFighterSide.Attacker && !readyAccepted);
        }
        if (display is not { } frame)
        {
            if (loop is { IsCompleted: false }) Widgets.Label(new Rect(x, 213, 240, 28), "Rimlink.GuildBattle.WaitingControl".Translate());
            return;
        }
        Widgets.Label(new Rect(x, 213, 240, 28), "Rimlink.GuildBattle.ZoneProgress".Translate(frame.HeldTicks, 3000));
        var own = Selected(frame);
        if (own is null || sending || commands?.Current != true) return;
        if (Widgets.ButtonText(new Rect(x, 245, 240, 28), (own.Drafted ? "Rimlink.GuildBattle.Undraft" : "Rimlink.GuildBattle.Draft").Translate()))
            Queue(new() { Version = 1, FighterId = own.Pawn.FighterId, Draft = !own.Drafted });
    }

    internal void DrawZone()
    {
        if (OnBattleMap) GenDraw.DrawFieldEdges(zone, Color.yellow);
    }

    internal void HandleMapClick()
    {
        if (!OnBattleMap || BattlePreparedViewer.Busy || sending || commands?.Current != true || display is not { } frame
            || Event.current is not { type: EventType.MouseDown, button: 1 } input || Selected(frame) is not { } own) return;
        var cell = Verse.UI.MouseCell();
        if (!cell.InBounds(map) || new Rect(Verse.UI.screenWidth - 250f, 55, 245, 220).Contains(input.mousePosition)) return;
        var enemy = frame.Pawns.FirstOrDefault(p => p.Pawn.Side != side && p.Spawned && p.Pawn.Alive && p.Pawn.X == cell.x && p.Pawn.Z == cell.z);
        var order = new BattleCommandV1 { Version = 1, FighterId = own.Pawn.FighterId };
        if (enemy is null) order.Move = new() { X = cell.x, Z = cell.z };
        else order.AttackFighterId = enemy.Pawn.FighterId;
        input.Use(); Queue(order);
    }

    private BattleRunningPawn? Selected(BattleRunningViewV1 frame) => Find.Selector.SingleSelectedThing is Pawn selected
        ? frame.Pawns.FirstOrDefault(p => p.Pawn.EntityId == selected.ThingID && p.Pawn.Side == side && p.Spawned && p.Pawn.Alive)
        : null;

    private void Queue(BattleCommandV1 command)
    {
        _ = Submit();
        async Task Submit()
        {
            try { await Order(command); }
            catch (Exception error) { RimlinkLog.Instance.Warn("battle order held: " + error.GetType().Name); }
        }
    }

    private static InvalidDataException Invalid() => new("battle-running-viewer-invalid");
}
