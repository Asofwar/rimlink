using HarmonyLib;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Adds "Multiplayer" right after "New colony" in the main menu.</summary>
[HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
public static class MainMenuPatch
{
    public static void Prefix(Rect rect, List<ListableOption> optList)
    {
        if (Current.ProgramState != ProgramState.Entry) return;
        string newColony = "NewColony".Translate();
        string label = "Rimlink.MainMenu.Multiplayer".Translate();
        int index = optList.FindIndex(o => o.label == newColony);
        if (index < 0 || optList.Any(o => o.label == label)) return;
        optList.Insert(index + 1, new ListableOption(label, () => Find.WindowStack.Add(new Dialog_ServerList())));
    }
}
