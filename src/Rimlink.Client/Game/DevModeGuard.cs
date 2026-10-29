using RimWorld;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>
/// Dev mode is off for a player the server does not allow it (integrity spec §3) while the session lasts: switched off when it
/// begins, refused by the setter (<see cref="Patches.Patch_Prefs_DevMode"/>), checked every frame of a game, and given back as
/// it was when the session ends, so single player keeps the setting. Main thread only.
/// </summary>
public static class DevModeGuard
{
    private static bool? restore;

    public static bool Blocked => RimlinkSession.Active && !RimlinkSession.DevModeAllowed;

    public static void OnSessionBegan()
    {
        if (!Blocked) return;
        restore ??= Prefs.DevMode;
        SwitchOff();
    }

    /// <summary>May run on any thread (a failed login ends the session in the background): the restore is posted.</summary>
    public static void OnSessionEnded()
    {
        UI.MainThread.Post(() =>
        {
            if (restore is not { } value || RimlinkSession.Active) return;
            restore = null;
            if (value) Prefs.DevMode = true;
        });
    }

    public static void Tick()
    {
        if (Blocked && Prefs.DevMode) SwitchOff();
    }

    private static void SwitchOff()
    {
        // The setter clears god mode and closes the dev palette; a debug tool left on the cursor from single player goes too.
        if (Prefs.DevMode)
        {
            Prefs.DevMode = false;
            RimlinkLog.Instance.Info("dev mode switched off for this session (not allowed on this server)");
        }
        LudeonTK.DebugTools.curTool = null;
    }
}
