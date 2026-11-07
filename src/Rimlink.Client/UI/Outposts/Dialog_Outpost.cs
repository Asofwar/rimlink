using Rimlink.Client.Game;
using Rimlink.Client.Game.Outposts;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Outposts;

/// <summary>Native owner controls; every command takes a fresh exact snapshot and leaves server validation authoritative.</summary>
public sealed class Dialog_Outpost(string? selectedId = null) : Window
{
    private readonly HashSet<string> recalled = [];
    private Vector2 scroll;
    private string? selected = selectedId;
    public override Vector2 InitialSize => new(720, 650);
    public override void PreOpen() { base.PreOpen(); forcePause = true; doCloseX = true; absorbInputAroundWindow = true; OutpostSync.Refresh(); }
    public override void DoWindowContents(Rect rect)
    {
        bool isolated = RimlinkSession.Login?.OutpostIsolatedAcceptance == true;
        Text.Font = isolated ? GameFont.Small : GameFont.Medium;
        Widgets.Label(new(0, 0, rect.width - 130, 38), (isolated ? "Rimlink.Outposts.TitleIsolated" : "Rimlink.Outposts.Title").Translate()); Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new(rect.width - 124, 0, 124, 32), "Rimlink.Outposts.Refresh".Translate(), active: !OutpostSync.Busy)) OutpostSync.Refresh();
        var rows = RimlinkSession.State.Outposts.Outposts.Where(o => o.Lifecycle != "Closed" && o.Lifecycle != "Aborted").ToArray();
        var row = rows.FirstOrDefault(o => o.OutpostId == selected) ?? rows.FirstOrDefault();
        if (row is null)
        {
            Widgets.Label(new(0, 55, rect.width, 100), "Rimlink.Outposts.Empty".Translate());
            DrawPending(180, rect.width); return;
        }
        if (selected != row.OutpostId) { selected = row.OutpostId; recalled.Clear(); }
        if (rows.Length > 1 && Widgets.ButtonText(new(0, 44, rect.width, 30), "Rimlink.Outposts.Marker".Translate(Resource(row.Profile))))
            Find.WindowStack.Add(new FloatMenu(rows.Select(o => new FloatMenuOption("Rimlink.Outposts.Tile".Translate(o.Tile), () => { selected = o.OutpostId; recalled.Clear(); })).ToList()));
        Widgets.Label(new(0, 82, rect.width, 26), "Rimlink.Outposts.Tile".Translate(row.Tile));
        Widgets.Label(new(0, 110, rect.width, 34), "Rimlink.Outposts.Summary".Translate(row.Stock, row.Capacity, row.UnitsPerHour.ToString("0.##")));
        string state = row.RecoveryRequired ? "SafetyHold" : row.Archived ? "Archived" : row.Stock >= row.Capacity ? "Full" : row.Lifecycle;
        Widgets.Label(new(0, 147, rect.width, 28), ("Rimlink.Outposts.State." + state).Translate());
        Widgets.Label(new(0, 178, rect.width, 40), (row.HasFraction ? "Rimlink.Outposts.FractionPresent" : "Rimlink.Outposts.FractionEmpty").Translate());
        var area = new Rect(0, 224, rect.width, 160);
        Widgets.BeginScrollView(area, ref scroll, new(0, 0, area.width - 20, Math.Max(area.height, row.Workers.Count * 38)));
        for (int i = 0; i < row.Workers.Count; i++)
        {
            var worker = row.Workers[i]; bool value = recalled.Contains(worker.PawnCustodyId);
            Widgets.CheckboxLabeled(new(0, i * 38, area.width - 24, 32), "Rimlink.Outposts.Worker".Translate(worker.Label, worker.UnitsPerHour.ToString("0.##")), ref value, disabled: OutpostSync.Busy);
            if (value) recalled.Add(worker.PawnCustodyId); else recalled.Remove(worker.PawnCustodyId);
        }
        Widgets.EndScrollView(); recalled.RemoveWhere(id => !row.Workers.Any(w => w.PawnCustodyId == id));
        bool ready = !OutpostSync.Busy && !OutpostOutgoing.Busy && row.Lifecycle is "Active" or "SafetyHold" && PawnCustodyRegistry.IsOwnGame && RimlinkSession.Login?.OutpostVersion == 1;
        float width = (rect.width - 12) / 2;
        if (Widgets.ButtonText(new(0, 398, width, 34), "Rimlink.Outposts.Claim".Translate(), active: ready && row.Stock > 0)) OutpostSync.Mutate(row, OutpostMutationKind.Claim);
        if (Widgets.ButtonText(new(width + 12, 398, width, 34), "Rimlink.Outposts.Recall".Translate(recalled.Count), active: ready && recalled.Count > 0))
        {
            var ids = recalled.ToArray();
            if (ids.Length == row.Workers.Count) Confirm("Rimlink.Outposts.CloseWarning", () => OutpostSync.Mutate(row, OutpostMutationKind.Recall, ids, confirmFractionDiscard: true));
            else OutpostSync.Mutate(row, OutpostMutationKind.Recall, ids);
        }
        if (Widgets.ButtonText(new(0, 442, width, 34), "Rimlink.Outposts.Switch".Translate(Resource(row.Profile == OutpostProfile.Wood ? OutpostProfile.Steel : OutpostProfile.Wood)), active: ready && row.Stock == 0 && row.Lifecycle == "Active"))
            Confirm("Rimlink.Outposts.FractionWarning", () => OutpostSync.Mutate(row, OutpostMutationKind.Switch, profile: row.Profile == OutpostProfile.Wood ? OutpostProfile.Steel : OutpostProfile.Wood, confirmFractionDiscard: true));
        if (Widgets.ButtonText(new(width + 12, 442, width, 34), "Rimlink.Outposts.Close".Translate(), active: ready))
            Confirm("Rimlink.Outposts.CloseWarning", () => OutpostSync.Mutate(row, OutpostMutationKind.Close, confirmFractionDiscard: true));
        DrawPending(487, rect.width);
        if (OutpostSync.LastFailure is not null) Widgets.Label(new(0, rect.height - 65, rect.width, 28), "Rimlink.Outposts.CommandHold".Translate());
        if (Widgets.ButtonText(new(0, rect.height - 32, rect.width, 32), "CloseButton".Translate())) Close();
    }
    private static string Resource(OutpostProfile profile) => (profile == OutpostProfile.Wood ? "Rimlink.Outposts.Wood" : "Rimlink.Outposts.Steel").Translate();
    private static void Confirm(string key, Action action) => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(key.Translate(), action));
    private static void DrawPending(float y, float width)
    {
        int pending = RimlinkSession.State.Outposts.PendingOperations.Count;
        if (pending == 0) return;
        Widgets.Label(new(0, y, width, 28), "Rimlink.Outposts.Pending".Translate(pending));
        var id = OutpostOutgoing.PendingAdmissionIds.FirstOrDefault();
        if (id is not null && Widgets.ButtonText(new(0, y + 30, width, 30), "Rimlink.Outposts.CancelAdmission".Translate(), active: !OutpostOutgoing.Busy)) OutpostOutgoing.Cancel(id);
    }
}
