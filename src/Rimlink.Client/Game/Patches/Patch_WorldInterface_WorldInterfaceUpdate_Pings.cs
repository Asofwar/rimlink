using HarmonyLib;
using RimWorld;

namespace Rimlink.Client.Game.Patches;

/// <summary>Ping marks on the world map, drawn every frame on real time (the world debug drawer counts game ticks).</summary>
[HarmonyPatch(typeof(WorldInterface), nameof(WorldInterface.WorldInterfaceUpdate))]
public static class Patch_WorldInterface_WorldInterfaceUpdate_Pings
{
    public static void Postfix() => Pings.PingMarkers.DrawOnWorld();
}
