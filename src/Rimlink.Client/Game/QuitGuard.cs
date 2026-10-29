using System.Reflection;
using UnityEngine;

namespace Rimlink.Client.Game;

/// <summary>
/// Quitting to the OS during a session first saves the colony and uploads it, as leaving to the menu does; the game quits
/// once the session is over. Without this, everything played since the last autosave was lost. Two ways in on RimWorld 1.7
/// (Waylith engine): the menu's Quit to OS ends in <see cref="Application.Quit()"/>, which asks
/// <see cref="Application.wantsToQuit"/>; the window's close button and Alt+F4 only set the GLFW window's close flag, so the
/// window's <c>OnClose</c> event cancels that and asks the same question on the next frame.
/// </summary>
public static class QuitGuard
{
    private static PropertyInfo? shouldClose;

    public static void Install()
    {
        Application.wantsToQuit += WantsToQuit;
        try
        {
            var appType = Type.GetType("Waylith.EngineApplication, Waylith");
            object? app = appType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            object? window = app is null ? null : appType!.GetProperty("Window")?.GetValue(app);
            var onClose = window?.GetType().GetEvent("OnClose");
            if (window is null || onClose?.EventHandlerType is not { } handlerType)
            {
                RimlinkLog.Instance.Warn("window close is not hooked (no Waylith window): closing the window during a session loses what was not saved");
                return;
            }
            shouldClose = window.GetType().GetProperty("ShouldClose");
            var method = typeof(QuitGuard).GetMethod(nameof(OnWindowClose), BindingFlags.NonPublic | BindingFlags.Static)!;
            onClose.AddEventHandler(window, Delegate.CreateDelegate(handlerType, method));
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn("window close is not hooked: " + ex.Message);
        }
    }

    public static bool WantsToQuit()
    {
        // Another quit request while the colony uploads: one flow, it quits at its end.
        if (SaveSync.Leaving)
        {
            SaveSync.SaveUploadAndEnd(Application.Quit);
            return false;
        }
        if (!RimlinkSession.Active) return true;
        if (!SaveSync.HasColonyToSave)
        {
            // A menu or new-colony page, or a visit: nothing of ours to save; log out and let the game quit now.
            RimlinkGameComponent.EndSession();
            return true;
        }
        RimlinkLog.Instance.Info("quitting: saving and uploading the colony first");
        SaveSync.SaveUploadAndEnd(Application.Quit);
        return false;
    }

    /// <summary>Inside the engine's event poll (main thread): keep the window open and decide on the next frame.</summary>
    private static void OnWindowClose(object window)
    {
        if (!RimlinkSession.Active && !SaveSync.Leaving) return;
        shouldClose?.SetValue(window, false);
        UI.MainThread.Post(() =>
        {
            if (WantsToQuit()) Application.Quit();
        });
    }
}
