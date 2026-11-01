using LudeonTK;
using Verse;

namespace Rimlink.Client.Game.Colonies;

public static class ColonyDebugActions
{
    [DebugAction("Rimlink", "Open shared colony", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Open()
    {
        if (RimlinkSession.Active) Find.WindowStack.Add(new UI.Colonies.Dialog_Colonies());
    }
}
