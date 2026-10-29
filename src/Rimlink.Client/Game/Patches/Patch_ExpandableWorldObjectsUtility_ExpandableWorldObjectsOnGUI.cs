using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>Draws the online light and the wealth bar on other players' expanded settlement icons, after the game draws the icons.</summary>
[HarmonyPatch(typeof(ExpandableWorldObjectsUtility), nameof(ExpandableWorldObjectsUtility.ExpandableWorldObjectsOnGUI))]
public static class Patch_ExpandableWorldObjectsUtility_ExpandableWorldObjectsOnGUI
{
    private static readonly Color Online = new(0.30f, 0.76f, 0.30f);
    private static readonly Color Offline = new(0.55f, 0.55f, 0.55f);
    private static readonly Color Outline = new(0.08f, 0.08f, 0.08f);
    private static readonly Color Filled = new(0.90f, 0.90f, 0.90f);
    private static readonly Color Empty = new(0.23f, 0.23f, 0.23f);

    public static void Postfix()
    {
        if (!RimlinkSession.Active || !DebugViewSettings.drawWorldObjects || Event.current.type != EventType.Repaint) return;
        var saved = GUI.color;
        try
        {
            foreach (var settlement in ForeignSettlements.All)
            {
                if (!settlement.Spawned || !settlement.def.expandingIcon) continue;
                float alpha = ExpandableWorldObjectsUtility.TransitionPct(settlement);
                if (alpha <= 0f || settlement.HiddenBehindTerrainNow()) continue;
                Draw(settlement, ExpandableWorldObjectsUtility.ExpandedIconScreenRect(settlement), alpha);
            }
        }
        finally
        {
            GUI.color = saved;
        }
    }

    private static void Draw(PlayerSettlement settlement, Rect icon, float alpha)
    {
        float dot = Mathf.Max(4f, icon.width * 0.3f);
        var light = new Rect(icon.xMax - dot * 0.75f, icon.y - dot * 0.25f, dot, dot);
        Fill(light.ExpandedBy(1f), Outline, alpha);
        Fill(light, settlement.OwnerOnline ? Online : Offline, alpha);

        if (settlement.WealthTier <= 0) return;
        const int segments = 5;
        float gap = 1f;
        float width = (icon.width - gap * (segments - 1)) / segments;
        float y = icon.yMax + 2f;
        for (int i = 0; i < segments; i++)
        {
            var segment = new Rect(icon.x + i * (width + gap), y, width, 3f);
            Fill(segment.ExpandedBy(0.5f), Outline, alpha);
            Fill(segment, i < settlement.WealthTier ? Filled : Empty, alpha);
        }
    }

    private static void Fill(Rect rect, Color color, float alpha)
    {
        GUI.color = new Color(color.r, color.g, color.b, alpha);
        GUI.DrawTexture(rect, BaseContent.WhiteTex);
    }
}
