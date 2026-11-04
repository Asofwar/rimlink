using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Outposts;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>The command is on the actual caravan: no remote placement and no workers removed before exact prepare.</summary>
public sealed class Dialog_OutpostStaffing(Caravan caravan) : Window
{
    private readonly HashSet<Pawn> selected = new(ReferenceEqualityComparer.Instance);
    private Vector2 scroll;
    private OutpostProfile profile = OutpostProfile.Wood;
    public override Vector2 InitialSize => new(650, 570);
    public override void PreOpen() { base.PreOpen(); forcePause = true; doCloseX = true; absorbInputAroundWindow = true; }
    public override void DoWindowContents(Rect rect)
    {
        Text.Font = GameFont.Medium; Widgets.Label(new(0, 0, rect.width, 38), "Rimlink.Outposts.StaffingTitle".Translate()); Text.Font = GameFont.Small;
        Widgets.Label(new(0, 44, rect.width, 66), "Rimlink.Outposts.StaffingHelp".Translate(caravan.Tile.tileId));
        if (Widgets.ButtonText(new(0, 116, rect.width, 32), (profile == OutpostProfile.Wood ? "Rimlink.Outposts.Wood" : "Rimlink.Outposts.Steel").Translate(), !OutpostOutgoing.Busy))
            profile = profile == OutpostProfile.Wood ? OutpostProfile.Steel : OutpostProfile.Wood;
        var pawns = caravan.PawnsListForReading.Where(p => p.IsFreeColonist && p.Faction == Faction.OfPlayer).ToArray();
        var area = new Rect(0, 158, rect.width, rect.height - 302);
        Widgets.BeginScrollView(area, ref scroll, new(0, 0, area.width - 18, Math.Max(area.height, pawns.Length * 36)));
        for (int i = 0; i < pawns.Length; i++)
        {
            var pawn = pawns[i]; bool value = selected.Contains(pawn);
            Widgets.CheckboxLabeled(new(0, i * 36, area.width - 22, 30), pawn.LabelShortCap, ref value, disabled: OutpostOutgoing.Busy);
            if (value) selected.Add(pawn); else selected.Remove(pawn);
        }
        Widgets.EndScrollView(); selected.RemoveWhere(p => !pawns.Any(x => ReferenceEquals(x, p)));
        var own = RimlinkSession.State.Outposts.Outposts.FirstOrDefault(o => !o.Archived && o.PawnCustodyIds.Count > 0);
        string? target = own?.Tile == caravan.Tile.tileId ? own.OutpostId : null;
        string status;
        bool eligible = true;
        try
        {
            var rates = selected.Select(p => new WorkerProductionPreview(p.skills.GetSkill(SkillDefOf.Plants).Level, p.skills.GetSkill(SkillDefOf.Mining).Level,
                p.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness), p.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation), p.health.capacities.GetLevel(PawnCapacityDefOf.Moving))).ToArray();
            var preview = OutpostAdmissionPreviews.Calculate(target is null ? profile : own!.Profile, rates, target is null, RimlinkSession.State.Economy.Balance);
            status = "Rimlink.Outposts.StaffingPreview".Translate(selected.Count, preview.UnitsPerHour.ToString("0.##"), preview.Capacity, preview.Cost, preview.WalletAfter);
            eligible = preview.WalletAfter >= 0;
        }
        catch (ArgumentException) { status = "Rimlink.Outposts.WorkerIneligible".Translate(); eligible = false; }
        if (OutpostOutgoing.Busy || OutpostOutgoing.LastFailure is not null) status = OutpostOutgoing.StatusKey.Translate();
        Widgets.Label(new(0, rect.height - 136, rect.width, 60), status);
        bool available = eligible && !OutpostOutgoing.Busy && selected.Count is >= 1 and <= 4 && caravan.Spawned && !caravan.pather.Moving;
        var pending = OutpostOutgoing.PendingAdmissionIds;
        if (pending.Count > 0)
        {
            if (Widgets.ButtonText(new(0, rect.height - 70, rect.width, 32), "Rimlink.Outposts.CancelAdmission".Translate(), !OutpostOutgoing.Busy))
                OutpostOutgoing.Cancel(pending[0]);
        }
        else if (Widgets.ButtonText(new(0, rect.height - 70, rect.width, 32), (target is null ? "Rimlink.Outposts.Found" : "Rimlink.Outposts.Assign").Translate(), available))
            OutpostOutgoing.Begin(caravan, selected.ToArray(), target is null ? profile : own!.Profile, target);
        if (Widgets.ButtonText(new(0, rect.height - 32, rect.width, 32), "CloseButton".Translate())) Close();
    }
}
