using HarmonyLib;
using Rimlink.Client.Game.Economy;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// A blob references things outside it by design (relations, memories, ideos); Scribe warns about each of them on every
/// blob save and load. Inside a blob pass those cross-reference warnings are counted instead of logged (see
/// <see cref="ThingBlobs"/>), so real warnings stay visible and the debug log does not open on them.
/// </summary>
[HarmonyPatch(typeof(Log), nameof(Log.Warning), typeof(string))]
public static class Patch_Log_Warning
{
    public static bool Prefix(string text) => !ThingBlobs.SwallowCrossRefWarning(text);
}
