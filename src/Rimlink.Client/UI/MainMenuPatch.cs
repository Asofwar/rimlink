using HarmonyLib;
using Rimlink.Client.Game;
using Rimlink.Client.Net;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>
/// Adds "Multiplayer" right after "New colony" in the main menu, and while a server's mod settings are in effect a line that
/// says so and switches back to the player's own. On the first menu frame of a launch it logs in again after "Fix and restart".
/// </summary>
[HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
public static class MainMenuPatch
{
    private static bool intentChecked;

    public static void Prefix(Rect rect, List<ListableOption> optList)
    {
        if (Current.ProgramState != ProgramState.Entry) return;
        string newColony = "NewColony".Translate();
        string label = "Rimlink.MainMenu.Multiplayer".Translate();
        int index = optList.FindIndex(o => o.label == newColony);
        if (index < 0 || optList.Any(o => o.label == label)) return;
        optList.Insert(index + 1, new ListableOption(label, () => Find.WindowStack.Add(new Dialog_ServerList())));
        if (ServerSettings.Active is { } active)
            optList.Insert(index + 2, new ListableOption("Rimlink.MainMenu.ServerSettings".Translate(active.Display), () => ConfirmOwnSettings(active.Display)));

        if (intentChecked) return;
        intentChecked = true;
        if (ModpackActions.JoinIntents.Take(DateTime.UtcNow) is not { } intent) return;
        var endpoint = RimlinkMod.Settings.Servers.Select(ServerEndpoint.FromSettingsLine)
            .FirstOrDefault(s => s is not null && s.Host == intent.Host && s.Port == intent.Port) ?? new ServerEndpoint(intent.Host, intent.Port, intent.Display);
        RimlinkLog.Instance.Info($"logging in to {endpoint.Display} again after the restart");
        MainThread.Post(() => _ = LoginFlow.Run(endpoint, new GameCredentials { PrefillLogin = intent.Login }));
    }

    private static void ConfirmOwnSettings(string server) =>
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.MainMenu.ServerSettingsConfirm".Translate(server), () =>
        {
            ServerSettings.Store.Clear();
            GenCommandLine.Restart();
        }));
}
