using HarmonyLib;
using RimWorld;

namespace Rimlink.Client.Game.Patches;

/// <summary>Ping marks on the map are drawn every frame, on real time, so they also pulse while the game is paused or mirrored.</summary>
[HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceUpdate))]
public static class Patch_MapInterface_MapInterfaceUpdate_Pings
{
    public static void Postfix() => Pings.PingMarkers.DrawOnMap();
}
