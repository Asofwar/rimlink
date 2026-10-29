using Grpc.Core;
using RimWorld;
using Rimlink.Client.Game;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>In-game chat: public channel and direct messages; "/" lines are admin commands.</summary>
public sealed class Dialog_Chat : Window
{
    private string input = "";
    private Vector2 scroll;
    private int lastCount = -1;
    private long targetPlayerId;
    private string targetName = "";

    public Dialog_Chat()
    {
        doCloseX = true;
        draggable = true;
        preventCameraMotion = false;
        absorbInputAroundWindow = false;
        closeOnClickedOutside = false;
        closeOnAccept = false;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(520f, 380f);

    /// <summary>Direct-message target, chosen in the players window. 0 = public.</summary>
    public void SetTarget(long playerId, string name)
    {
        targetPlayerId = playerId;
        targetName = name;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var messages = RimlinkSession.State.Chat.Messages;
        var listRect = new Rect(0f, 0f, inRect.width, inRect.height - 70f);
        var text = string.Join("\n", messages.Select(Format));
        float height = Text.CalcHeight(text, listRect.width - 16f);
        if (messages.Count != lastCount) { scroll.y = Math.Max(0f, height - listRect.height); lastCount = messages.Count; }
        Widgets.BeginScrollView(listRect, ref scroll, new Rect(0f, 0f, listRect.width - 16f, Math.Max(height, listRect.height)));
        Widgets.Label(new Rect(0f, 0f, listRect.width - 16f, height), text);
        Widgets.EndScrollView();

        float y = inRect.height - 62f;
        string targetLabel = targetPlayerId == 0 ? "Rimlink.Chat.Public".Translate() : "Rimlink.Chat.To".Translate(targetName);
        if (Widgets.ButtonText(new Rect(0f, y, 150f, 26f), targetLabel)) SetTarget(0, "");
        y += 30f;
        GUI.SetNextControlName("RimlinkChatInput");
        input = Widgets.TextField(new Rect(0f, y, inRect.width - 90f, 28f), input, Limits.ChatMaxLength);
        bool enter = Event.current.type == EventType.KeyDown && Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter && GUI.GetNameOfFocusedControl() == "RimlinkChatInput";
        if (Widgets.ButtonText(new Rect(inRect.width - 84f, y, 84f, 28f), "Rimlink.Chat.Send".Translate()) || enter)
        {
            if (enter) Event.current.Use();
            Send();
        }
    }

    private void Send()
    {
        string line = input.Trim();
        if (line.Length == 0 || !RimlinkSession.Active) return;
        input = "";
        var connection = RimlinkSession.Connection!;
        long to = targetPlayerId;
        _ = Task.Run(async () =>
        {
            try { await connection.Chat.SendAsync(new SendRequest { Text = line, ToPlayerId = to }); }
            catch (RpcException ex)
            {
                MainThread.Post(() => Messages.Message(ex.Status.Detail, MessageTypeDefOf.RejectInput, historical: false));
            }
        });
    }

    private static string Format(ChatMessageEvent m)
    {
        string time = m.At?.ToDateTime().ToLocalTime().ToString("HH:mm") ?? "";
        string body = Escape(m.Text);
        if (m.Kind == MessageKind.System) return $"<color=#9aa4b5>[{time}] {body}</color>";
        if (m.Kind == MessageKind.External) return $"[{time}] <b>{"Rimlink.Chat.External".Translate(Escape(m.ExternalAuthor))}</b>: {body}";
        string who = Escape(m.From?.Name ?? "?");
        string dm = m.ToPlayerId != 0 ? " <color=#c8a2ff>(dm)</color>" : "";
        return $"[{time}] <b>{who}</b>{dm}: {body}";
    }

    /// <summary>Player text must not inject rich-text tags.</summary>
    private static string Escape(string text) => text.Replace("<", "<​");
}
