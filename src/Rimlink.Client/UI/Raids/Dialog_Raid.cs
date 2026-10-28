using RimWorld;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Raids;

/// <summary>Who goes, how (real pawns or copies) and when (warned or instant). Pods carry everyone and only real pawns.</summary>
public sealed class Dialog_Raid : Window
{
    private readonly PlayerRef target;
    private readonly List<Pawn> candidates;
    private readonly HashSet<Pawn> chosen;
    private readonly bool fromPods;
    private readonly Action<IReadOnlyList<Pawn>, PawnMode, RaidTiming> onLaunch;
    private readonly Action? onCancel;
    private PawnMode mode = PawnMode.PawnReal;
    private RaidTiming timing = RaidTiming.TimingWarned;
    private Vector2 scroll;
    private bool launched;

    public override Vector2 InitialSize => new(520f, 520f);

    public Dialog_Raid(PlayerRef target, List<Pawn> candidates, bool fromPods, Action<IReadOnlyList<Pawn>, PawnMode, RaidTiming> onLaunch, Action? onCancel)
    {
        this.target = target;
        this.candidates = candidates;
        this.fromPods = fromPods;
        this.onLaunch = onLaunch;
        this.onCancel = onCancel;
        chosen = fromPods ? [.. candidates] : [];
        doCloseX = !fromPods;
        closeOnClickedOutside = false;
        forcePause = true;
        absorbInputAroundWindow = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "Rimlink.Raid.Title".Translate(target.Name));
        Text.Font = GameFont.Small;
        float y = 40f;
        Widgets.Label(new Rect(0f, y, inRect.width, 44f), fromPods ? "Rimlink.Raid.PodsIntro".Translate() : "Rimlink.Raid.Intro".Translate());
        y += 48f;

        var listRect = new Rect(0f, y, inRect.width, 200f);
        Widgets.BeginScrollView(listRect, ref scroll, new Rect(0f, 0f, listRect.width - 16f, Math.Max(listRect.height, candidates.Count * 28f)));
        float ly = 0f;
        foreach (var pawn in candidates)
        {
            bool on = chosen.Contains(pawn);
            if (fromPods) Widgets.Label(new Rect(0f, ly, listRect.width - 16f, 26f), pawn.LabelCap);
            else
            {
                Widgets.CheckboxLabeled(new Rect(0f, ly, listRect.width - 16f, 26f), pawn.LabelCap, ref on);
                if (on) chosen.Add(pawn); else chosen.Remove(pawn);
            }
            ly += 28f;
        }
        Widgets.EndScrollView();
        y += 208f;

        if (!fromPods)
        {
            Widgets.Label(new Rect(0f, y, 120f, 26f), "Rimlink.Raid.Mode".Translate());
            if (Widgets.RadioButtonLabeled(new Rect(120f, y, 180f, 26f), "Rimlink.Raid.ModeReal".Translate(), mode == PawnMode.PawnReal)) mode = PawnMode.PawnReal;
            if (Widgets.RadioButtonLabeled(new Rect(310f, y, 200f, 26f), "Rimlink.Raid.ModeSnapshot".Translate(), mode == PawnMode.PawnSnapshot)) mode = PawnMode.PawnSnapshot;
            y += 30f;
        }
        Widgets.Label(new Rect(0f, y, 120f, 26f), "Rimlink.Raid.Timing".Translate());
        if (Widgets.RadioButtonLabeled(new Rect(120f, y, 180f, 26f), "Rimlink.Raid.TimingWarned".Translate(), timing == RaidTiming.TimingWarned)) timing = RaidTiming.TimingWarned;
        if (Widgets.RadioButtonLabeled(new Rect(310f, y, 200f, 26f), "Rimlink.Raid.TimingInstant".Translate(), timing == RaidTiming.TimingInstant)) timing = RaidTiming.TimingInstant;
        y += 34f;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, y, inRect.width, 44f), "Rimlink.Raid.LootNote".Translate());
        Text.Font = GameFont.Small;

        if (Widgets.ButtonText(new Rect(0f, inRect.height - 36f, 200f, 32f), "Rimlink.Raid.Launch".Translate()) && chosen.Count > 0)
        {
            var pawns = candidates.Where(chosen.Contains).ToList();
            Action go = () => { launched = true; onLaunch(pawns, mode, timing); Close(); };
            if (mode == PawnMode.PawnReal && pawns.Any(p => p.RaceProps.Humanlike))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.Raid.ConfirmReal".Translate(string.Join(", ", pawns.Select(p => p.LabelShort))), go, destructive: true));
            else go();
        }
        if (Widgets.ButtonText(new Rect(inRect.width - 160f, inRect.height - 36f, 160f, 32f), "Rimlink.Raid.Cancel".Translate())) Close();
    }

    public override void PreClose()
    {
        base.PreClose();
        if (!launched) onCancel?.Invoke();
    }
}
