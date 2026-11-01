using LudeonTK;
using Verse;

namespace Rimlink.Client.Game;

public static class CommunityDebugActions
{
    [DebugAction("Rimlink", "Open social window", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void OpenSocialWindow()
    {
        if (!RimlinkSession.Active) { Log.Message("[Rimlink] no session"); return; }
        Find.WindowStack.Add(new UI.Dialog_Community());
    }
}
