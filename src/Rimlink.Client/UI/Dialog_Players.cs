using Rimlink.Client.Game;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Who is online; a click starts a direct message in the chat window.</summary>
public sealed class Dialog_Players : Window
{
    private Vector2 scroll;

    public Dialog_Players()
    {
        doCloseX = true;
        draggable = true;
        preventCameraMotion = false;
        absorbInputAroundWindow = false;
        closeOnClickedOutside = false;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(320f, 400f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), "Rimlink.Players.Title".Translate(RimlinkSession.State.Players.Online.Count));
        Text.Font = GameFont.Small;
        var players = RimlinkSession.State.Players.Online.OrderBy(p => p.Name).ToList();
        var listRect = new Rect(0f, 44f, inRect.width, inRect.height - 44f);
        Widgets.BeginScrollView(listRect, ref scroll, new Rect(0f, 0f, listRect.width - 16f, Math.Max(listRect.height, players.Count * 30f)));
        float y = 0f;
        foreach (var p in players)
        {
            var row = new Rect(0f, y, listRect.width - 16f, 28f);
            Widgets.Label(new Rect(0f, y + 3f, row.width - 90f, 24f), $"{p.Name}  ({p.FactionName})");
            if (p.PlayerId != RimlinkSession.MyPlayerId && Widgets.ButtonText(new Rect(row.xMax - 86f, y, 86f, 26f), "Rimlink.Players.Message".Translate()))
            {
                var chat = Find.WindowStack.WindowOfType<Dialog_Chat>() ?? Open<Dialog_Chat>();
                chat.SetTarget(p.PlayerId, p.Name);
            }
            y += 30f;
        }
        Widgets.EndScrollView();
    }

    private static T Open<T>() where T : Window, new()
    {
        var window = new T();
        Find.WindowStack.Add(window);
        return window;
    }
}
