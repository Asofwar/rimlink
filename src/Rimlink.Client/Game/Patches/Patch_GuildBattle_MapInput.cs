using HarmonyLib;
using Rimlink.Client.Game.GuildCapture;
using RimWorld;
using RimWorld.Planet;

namespace Rimlink.Client.Game.Patches;

// The typed viewer consumes its own commands before vanilla selection and job menus.
[HarmonyPatch(typeof(MapInterface), nameof(MapInterface.HandleMapClicks))]
public static class Patch_MapInterface_HandleMapClicks_GuildBattle
{
    public static void Prefix()
    {
        if (WorldRendererUtility.DrawingMap) BattlePreparedViewer.HandleMapClick();
    }
}

[HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceUpdate))]
public static class Patch_MapInterface_Update_GuildBattle
{
    public static void Postfix()
    {
        if (WorldRendererUtility.DrawingMap) BattlePreparedViewer.DrawZone();
    }
}
