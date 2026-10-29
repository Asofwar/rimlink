using Grpc.Core;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Chat;
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

    private const float CardButtonHeight = 22f;

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var messages = RimlinkSession.State.Chat.Messages;
        var listRect = new Rect(0f, 0f, inRect.width, inRect.height - 70f);
        float width = listRect.width - 16f;
        // One row per message: a message with cards gets its link buttons under its text.
        var rows = messages.Select(m => (Text: Format(m), Cards: m.Kind == MessageKind.System ? [] : ChatCards.Parse(m.Text))).ToList();
        var heights = rows.Select(r => Text.CalcHeight(r.Text, width) + (r.Cards.Count > 0 ? CardButtonHeight + 2f : 0f)).ToList();
        float height = heights.Sum();
        if (messages.Count != lastCount) { scroll.y = Math.Max(0f, height - listRect.height); lastCount = messages.Count; }
        Widgets.BeginScrollView(listRect, ref scroll, new Rect(0f, 0f, width, Math.Max(height, listRect.height)));
        float rowY = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            float textHeight = heights[i] - (rows[i].Cards.Count > 0 ? CardButtonHeight + 2f : 0f);
            Widgets.Label(new Rect(0f, rowY, width, textHeight), rows[i].Text);
            float x = 12f;
            foreach (var card in rows[i].Cards)
            {
                // As wide as the label (the owner's name tells two colonies apart), cut only at the row's end.
                string label = CardLabel(card.Tile);
                float w = Math.Min(Text.CalcSize(label).x + 16f, width - x);
                if (w < 40f) break;
                if (Widgets.ButtonText(new Rect(x, rowY + textHeight, w, CardButtonHeight), label.Truncate(w - 4f))) JumpTo(card.Tile);
                x += w + 4f;
            }
            rowY += heights[i];
        }
        Widgets.EndScrollView();

        float y = inRect.height - 62f;
        string targetLabel = targetPlayerId == 0 ? "Rimlink.Chat.Public".Translate() : "Rimlink.Chat.To".Translate(targetName);
        if (Widgets.ButtonText(new Rect(0f, y, 150f, 26f), targetLabel)) SetTarget(0, "");
        var placeRect = new Rect(156f, y, 100f, 26f);
        TooltipHandler.TipRegion(placeRect, "Rimlink.Chat.PlaceTip".Translate());
        if (Widgets.ButtonText(placeRect, "Rimlink.Chat.Place".Translate()) && SelectedTile() is { } tile)
            input = (input.Length == 0 || input.EndsWith(" ") ? input : input + " ") + ChatCards.Token(tile);
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
        if (targetPlayerId != 0 && RimlinkSession.State.Social.IsBlocked(targetPlayerId))
        {
            Messages.Message("Rimlink.Social.YouBlocked".Translate(targetName), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        input = "";
        var connection = RimlinkSession.Connection!;
        long to = targetPlayerId;
        _ = Task.Run(async () =>
        {
            try { await connection.Chat.SendAsync(new SendRequest { Text = line, ToPlayerId = to }); }
            catch (RpcException ex)
            {
                MainThread.Post(() => Messages.Message(RefusalText.Of(ex), MessageTypeDefOf.RejectInput, historical: false));
            }
        });
    }

    /// <summary>The place a card would name: what is selected on the world map, else the tile of the map shown.</summary>
    private static int? SelectedTile()
    {
        if (Find.World is null) return null;
        if (Find.WorldSelector.SelectedObjects.FirstOrDefault() is { } selected) return selected.Tile;
        if (Find.WorldSelector.SelectedTile.Valid) return Find.WorldSelector.SelectedTile;
        return Find.CurrentMap?.Tile;
    }

    /// <summary>A card's text in this player's world: the settlement on the tile with its owner, else the tile and its biome.</summary>
    private static string CardLabel(int tile)
    {
        if (Find.World is not { } world || tile < 0 || tile >= world.grid.TilesCount) return "Rimlink.Chat.CardTileBare".Translate(tile);
        // A player's colony is Rimlink's own world object, not a vanilla Settlement: its name and owner come from the server's index.
        if (RimlinkSession.State.Objects.All.FirstOrDefault(o => o.Tile == tile && o.Owner is not null) is { } colony)
            return "Rimlink.Chat.CardSettlement".Translate(colony.Name, colony.Owner.Name);
        if (Find.WorldObjects.SettlementAt(tile) is { } settlement)
            return "Rimlink.Chat.CardSettlement".Translate(settlement.LabelCap, settlement.Faction?.Name ?? "?");
        return "Rimlink.Chat.CardTile".Translate(tile, world.grid[tile].PrimaryBiome?.label ?? "?");
    }

    private static void JumpTo(int tile)
    {
        if (Find.World is not { } world || tile < 0 || tile >= world.grid.TilesCount) return;
        if (Find.WorldObjects.ObjectsAt(tile).FirstOrDefault() is { } place) CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(place));
        else CameraJumper.TryJump(new GlobalTargetInfo((PlanetTile)tile));
    }

    /// <summary>The message text with each card token shown as its label (escaped like the rest of the text).</summary>
    private static string Body(string text)
    {
        var cards = ChatCards.Parse(text);
        if (cards.Count == 0) return Escape(text);
        var body = new System.Text.StringBuilder();
        int at = 0;
        foreach (var card in cards)
        {
            body.Append(Escape(text[at..card.Start])).Append("<color=#8cc8ff>").Append(Escape(CardLabel(card.Tile))).Append("</color>");
            at = card.Start + card.Length;
        }
        return body.Append(Escape(text[at..])).ToString();
    }

    private static string Format(ChatMessageEvent m)
    {
        string time = m.At?.ToDateTime().ToLocalTime().ToString("HH:mm") ?? "";
        string body = m.Kind == MessageKind.System ? Escape(m.Text) : Body(m.Text);
        if (m.Kind == MessageKind.System) return $"<color=#9aa4b5>[{time}] {body}</color>";
        if (m.Kind == MessageKind.External) return $"[{time}] <b>{"Rimlink.Chat.External".Translate(Escape(m.ExternalAuthor))}</b>: {body}";
        string who = Escape(m.From?.Name ?? "?");
        string dm = m.ToPlayerId != 0 ? " <color=#c8a2ff>(dm)</color>" : "";
        return $"[{time}] <b>{who}</b>{dm}: {body}";
    }

    /// <summary>Player text must not inject rich-text tags.</summary>
    private static string Escape(string text) => text.Replace("<", "<​");
}
