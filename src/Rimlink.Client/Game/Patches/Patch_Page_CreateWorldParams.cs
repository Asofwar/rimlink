using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>While joining a server whose world exists, the world-creation page is locked to the server's parameters.</summary>
[HarmonyPatch(typeof(Page_CreateWorldParams))]
public static class Patch_Page_CreateWorldParams
{
    private static bool Locked => RimlinkSession.Active && RimlinkSession.World is { Exists: true };

    [HarmonyPostfix, HarmonyPatch(nameof(Page_CreateWorldParams.PostOpen))]
    public static void PostOpen(Page_CreateWorldParams __instance)
    {
        if (Locked) WorldSetup.ApplyTo(__instance, RimlinkSession.World!.Params);
    }

    [HarmonyPrefix, HarmonyPatch(nameof(Page_CreateWorldParams.DoWindowContents))]
    public static void DoWindowContents(Page_CreateWorldParams __instance, Rect rect)
    {
        if (!Locked) return;
        // Re-apply every frame so edits in the UI cannot drift from the server's world.
        WorldSetup.ApplyTo(__instance, RimlinkSession.World!.Params);
        Text.Font = GameFont.Small;
        // In the empty strip above the bottom buttons; the vanilla page fills the top with its title and controls.
        GUI.color = new Color(1f, 0.85f, 0.4f);
        Widgets.Label(new Rect(rect.x, rect.yMax - 72f, rect.width, 24f), "Rimlink.World.Locked".Translate(RimlinkSession.Endpoint!.Display));
        GUI.color = Color.white;
    }
}
