using RimWorld;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Game;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Economy;

/// <summary>Checkbox list of launchable things (under trade beacons) with per-stack counts, plus colonists and animals.</summary>
public sealed class ThingPicker(Map map, bool allowPawns)
{
    private readonly Dictionary<Thing, int> counts = [];
    private readonly Dictionary<Thing, string> buffers = [];
    private readonly HashSet<Pawn> pawns = [];
    private Vector2 scroll;

    public IReadOnlyList<Outgoing.Pick> Picks => counts.Where(kv => kv.Value > 0 && kv.Key.Spawned).Select(kv => new Outgoing.Pick(kv.Key, Math.Min(kv.Value, kv.Key.stackCount))).ToList();
    public IReadOnlyList<Pawn> Pawns => pawns.Where(p => p.Spawned).ToList();
    public bool AnythingPicked => Picks.Count > 0 || Pawns.Count > 0;

    public void Clear()
    {
        counts.Clear();
        buffers.Clear();
        pawns.Clear();
    }

    public void Draw(Rect rect)
    {
        var source = BlobDebugActions.PickAnywhere && Prefs.DevMode
            ? map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.Spawned && !t.Position.Fogged(map))
            : TradeUtility.AllLaunchableThingsForTrade(map);
        var things = source.Where(t => t.def != ThingDefOf.Silver).OrderBy(t => t.LabelCapNoCount).ToList();
        var colonists = allowPawns ? map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.SpawnedColonyAnimals).ToList() : [];
        float height = (things.Count + colonists.Count) * 28f + (allowPawns ? 30f : 0f) + 30f;
        Widgets.BeginScrollView(rect, ref scroll, new Rect(0f, 0f, rect.width - 16f, Math.Max(height, rect.height)));
        float y = 0f;
        float w = rect.width - 16f;
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(0f, y, w, 26f), "Rimlink.Economy.ItemsUnderBeacons".Translate(things.Count));
        y += 28f;
        if (things.Count == 0)
        {
            Widgets.Label(new Rect(0f, y, w, 26f), "Rimlink.Economy.NoBeacon".Translate());
            y += 28f;
        }
        foreach (var thing in things)
        {
            bool on = counts.TryGetValue(thing, out int n) && n > 0;
            bool was = on;
            bool forbidden = Forbidden(thing.def.defName) || Forbidden(thing.Stuff?.defName);
            if (forbidden) on = false;
            var row = new Rect(0f, y, w - 120f, 26f);
            Widgets.CheckboxLabeled(row, $"{thing.LabelCapNoCount} ×{thing.stackCount}", ref on, disabled: forbidden);
            if (forbidden) TooltipHandler.TipRegion(row, "Rimlink.Economy.Forbidden".Translate());
            if (on != was) counts[thing] = on ? thing.stackCount : 0;
            if (on)
            {
                int count = counts[thing];
                string buffer = buffers.GetValueOrDefault(thing) ?? count.ToString();
                Widgets.TextFieldNumeric(new Rect(w - 110f, y, 100f, 26f), ref count, ref buffer, 1, thing.stackCount);
                counts[thing] = count;
                buffers[thing] = buffer;
            }
            y += 28f;
        }
        if (allowPawns)
        {
            y += 2f;
            Widgets.Label(new Rect(0f, y, w, 26f), "Rimlink.Economy.Pawns".Translate());
            y += 28f;
            foreach (var pawn in colonists)
            {
                bool on = pawns.Contains(pawn);
                bool forbidden = Forbidden(pawn.kindDef.defName);
                if (forbidden) on = false;
                var row = new Rect(0f, y, w, 26f);
                Widgets.CheckboxLabeled(row, pawn.LabelCap + (pawn.RaceProps.Humanlike ? "" : $" ({pawn.KindLabel})"), ref on, disabled: forbidden);
                if (forbidden) TooltipHandler.TipRegion(row, "Rimlink.Economy.Forbidden".Translate());
                if (on) pawns.Add(pawn); else pawns.Remove(pawn);
                y += 28f;
            }
        }
        Widgets.EndScrollView();
    }

    /// <summary>The admin forbade this def in sends and lots (the server refuses it anyway).</summary>
    private static bool Forbidden(string? defName) => defName is not null && RimlinkSession.State.Economy.IsForbidden(defName);
}
