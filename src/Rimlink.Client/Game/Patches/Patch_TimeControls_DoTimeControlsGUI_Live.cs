using HarmonyLib;
using RimWorld;
using Rimlink.Client.Game.Live;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// Speed votes on the time buttons during a live session (live UX spec §4): a small mark in the corner of the speed each side asked for
/// (host top-left, guest bottom-right), and a tooltip with both wishes and the speed the game runs at (the slower one).
/// </summary>
[HarmonyPatch(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI))]
public static class Patch_TimeControls_DoTimeControlsGUI_Live
{
    public static void Postfix(Rect timerRect)
    {
        int host, guest, effective;
        if (LiveHost.Active) (host, guest, effective) = (LiveTempo.Rules.HostWish, LiveTempo.Rules.GuestWish, LiveTempo.Rules.Effective);
        else if (MirrorMode.Active) (host, guest, effective) = (MirrorMode.HostWish, MirrorMode.GuestWish, MirrorMode.PausedBy != 0 ? 0 : Find.TickManager is { } tm ? (int)tm.CurTimeSpeed : 0);
        else return;
        // The buttons sit left to right in TimeSpeed order (Ultrafast is not drawn), TimeButSize each: button i is speed i.
        var size = TimeControls.TimeButSize;
        var font = Text.Font;
        var anchor = Text.Anchor;
        Text.Font = GameFont.Tiny;
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = new Color(1f, 0.85f, 0.3f);
        Widgets.Label(new Rect(timerRect.x + Mathf.Clamp(host, 0, 3) * size.x + 1f, timerRect.y - 2f, 14f, 14f), "Rimlink.Live.MarkHost".Translate());
        Text.Anchor = TextAnchor.LowerRight;
        GUI.color = new Color(0.55f, 0.8f, 1f);
        Widgets.Label(new Rect(timerRect.x + (Mathf.Clamp(guest, 0, 3) + 1) * size.x - 15f, timerRect.y + size.y - 13f, 14f, 14f), "Rimlink.Live.MarkGuest".Translate());
        GUI.color = Color.white;
        Text.Anchor = anchor;
        Text.Font = font;
        TooltipHandler.TipRegion(timerRect, "Rimlink.Live.SpeedVotes".Translate(Symbol(host), Symbol(guest), Symbol(effective)));
    }

    private static string Symbol(int speed) => speed switch { <= 0 => "‖", 1 => "▶", 2 => "▶▶", _ => "▶▶▶" };
}
