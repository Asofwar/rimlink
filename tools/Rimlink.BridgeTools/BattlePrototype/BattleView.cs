using Rimlink.BattlePrototype;
using UnityEngine;
using Verse;

namespace Rimlink.BridgeTools.BattlePrototype;

internal sealed class BattleViewWindow(BattleRuntime runtime) : Window
{
    private string selected = runtime.Binding.Role == BattleRole.SideA ? "a1" : "b1";
    private string x = "124", z = "124", message = "";
    public override Vector2 InitialSize => new(900, 700);

    public override void DoWindowContents(Rect inRect)
    {
        var view = runtime.View;
        Widgets.Label(new Rect(0, 0, 860, 28), $"Battle prototype | {runtime.Binding.Role} | {runtime.Phase} | {runtime.Error ?? message}");
        if (view is null) { Widgets.Label(new Rect(0, 35, 850, 30), "Waiting for arbiter state"); return; }
        Widgets.Label(new Rect(0, 32, 860, 26), $"Tick {view.State.Tick} | Zone {view.State.HeldTicks}/{BattleRules.CaptureTicks} | {view.State.Outcome} | View {view.Sequence}");
        var grid = new Rect(0, 70, 500, 500);
        Widgets.DrawBoxSolid(grid, new Color(.12f, .14f, .16f));
        Rect Cell(int cx, int cz, int width = 1, int height = 1) => new(grid.x + (cx - view.Arena.MinX) * 10,
            grid.y + (view.Arena.MaxZ - cz - height + 1) * 10, width * 10, height * 10);
        Widgets.DrawBoxSolid(Cell(view.Zone.MinX, view.Zone.MinZ, view.Zone.MaxX - view.Zone.MinX + 1, view.Zone.MaxZ - view.Zone.MinZ + 1), new Color(.25f, .35f, .2f));
        foreach (var pawn in view.Pawns)
        {
            var box = Cell(pawn.X, pawn.Z);
            Widgets.DrawBoxSolid(box, !pawn.CombatReady ? Color.gray : pawn.Side == BattleSide.Attacker ? Color.red : Color.cyan);
            Widgets.Label(new Rect(box.x, box.y - 16, 40, 22), pawn.Id);
        }
        int row = 0;
        foreach (var pawn in view.Pawns)
            Widgets.Label(new Rect(520, 75 + row++ * 40, 330, 38), $"{pawn.Id} ({pawn.X},{pawn.Z}) HP {pawn.Health:P0} | {(pawn.CombatReady ? "ready" : pawn.Alive ? "incapable" : "dead")}");
        var side = runtime.Binding.Role == BattleRole.SideA ? BattleSide.Attacker : BattleSide.Defender;
        int index = 0;
        foreach (var pawn in view.Pawns.Where(p => p.Side == side))
            if (Widgets.ButtonText(new Rect(index++ * 90, 585, 82, 30), "Select " + pawn.Id)) selected = pawn.Id;
        Widgets.Label(new Rect(375, 588, 80, 26), selected);
        x = Widgets.TextField(new Rect(450, 585, 60, 30), x);
        z = Widgets.TextField(new Rect(520, 585, 60, 30), z);
        if (Widgets.ButtonText(new Rect(590, 585, 100, 30), "Move"))
        {
            if (int.TryParse(x, out int cx) && int.TryParse(z, out int cz)) Send(new(runtime.NextSequence, BattleCommandKind.MoveTo, selected, cx, cz));
            else message = "Integer coordinates required";
        }
        index = 0;
        foreach (var target in view.Pawns.Where(p => p.Side != side))
            if (Widgets.ButtonText(new Rect(index++ * 130, 625, 120, 30), "Attack " + target.Id))
                Send(new(runtime.NextSequence, BattleCommandKind.Attack, selected, TargetPawnId: target.Id));
        if (Widgets.ButtonText(new Rect(590, 625, 100, 30), "Draft")) Send(new(runtime.NextSequence, BattleCommandKind.Draft, selected));
        if (Widgets.ButtonText(new Rect(705, 625, 120, 30), "Undraft")) Send(new(runtime.NextSequence, BattleCommandKind.Draft, selected, Drafted: false));
    }

    private async void Send(BattleCommand command)
    {
        try
        {
            var reply = await runtime.Order(command);
            Rimlink.Client.UI.MainThread.Post(() => message = $"Command {command.Sequence}: {reply.Code}");
        }
        catch (Exception ex) { Rimlink.Client.UI.MainThread.Post(() => message = ex.Message); }
    }
}
