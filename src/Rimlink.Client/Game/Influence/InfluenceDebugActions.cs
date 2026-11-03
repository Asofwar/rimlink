using Rimlink.Client.UI;
using LudeonTK;
using Verse;

namespace Rimlink.Client.Game.Influence;

public static class InfluenceDebugActions
{
    [DebugAction("Rimlink", "Open influence settings", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Settings() { if (RimlinkSession.Active) InfluenceOverlay.OpenSettings(); }

    [DebugAction("Rimlink", "Refresh influence snapshot", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Refresh() { if (RimlinkSession.Active) RimlinkSession.State.Influence.Invalidate(); }
}
