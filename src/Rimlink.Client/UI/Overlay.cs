using Rimlink.Client.Game;
using Rimlink.Client.Net;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Two small buttons in the top-right corner while connected: chat and players, plus the connection status.</summary>
public static class Overlay
{
    public static void Draw()
    {
        if (!RimlinkSession.Active || Current.ProgramState != ProgramState.Playing) return;
        float x = Verse.UI.screenWidth - 230f;
        float y = 60f;
        if (Game.Live.MirrorMode.Active)
        {
            y += Live.LiveHud.DrawGuest(x, y);
        }
        else if (Game.Visits.VisitSession.Active)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(x, y, 220f, 24f), "Rimlink.Visit.Hud".Translate(Game.Visits.VisitSession.HostName));
            if (Widgets.ButtonText(new Rect(x, y + 26f, 220f, 26f), "Rimlink.Visit.End".Translate())) Game.Visits.VisitSession.End();
            y += 56f;
        }
        y += Live.LiveHud.DrawHost(x, y);
        Text.Font = GameFont.Tiny;
        string status = RimlinkSession.LastState switch
        {
            ConnectionState.Connected => "Rimlink.Overlay.Online".Translate(RimlinkSession.State.Players.Online.Count),
            ConnectionState.Reconnecting => "Rimlink.Net.Reconnecting".Translate(),
            var other => other.ToString(),
        };
        Widgets.Label(new Rect(x, y, 220f, 20f), $"<color=#9aa4b5>{status}</color>");
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(x, y + 22f, 106f, 26f), "Rimlink.Overlay.Chat".Translate()))
        {
            if (Find.WindowStack.WindowOfType<Dialog_Chat>() is { } chat) chat.Close();
            else Find.WindowStack.Add(new Dialog_Chat());
        }
        if (Widgets.ButtonText(new Rect(x + 114f, y + 22f, 106f, 26f), "Rimlink.Overlay.Players".Translate()))
        {
            if (Find.WindowStack.WindowOfType<Dialog_Players>() is { } players) players.Close();
            else Find.WindowStack.Add(new Dialog_Players());
        }
    }
}
