using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Economy;

/// <summary>
/// The trades tab of the economy window: the player's trades on the left, the selected one's two sides on the right, with
/// "change my side", "confirm" and "cancel". A new trade starts by picking a partner (an offline one too: anyone with a settlement).
/// </summary>
public sealed class TradesTab
{
    private long selectedId;
    private Vector2 listScroll, cardScroll;
    private string status = "";
    private bool busy;

    public void Select(long tradeId) => selectedId = tradeId;

    public void Draw(Rect rect, Map? map, IEnumerable<PlayerRef> knownPlayers)
    {
        var social = RimlinkSession.State.Social;
        if (Widgets.ButtonText(new Rect(0f, rect.y, 200f, 28f), "Rimlink.Trades.New".Translate()))
        {
            var options = knownPlayers.Where(p => !social.IsBlocked(p.PlayerId))
                .Select(p => new FloatMenuOption(p.Name, () => StartNew(map, p))).ToList();
            if (options.Count == 0) options.Add(new FloatMenuOption("Rimlink.Economy.NobodyKnown".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(options));
        }
        GUI.color = Color.gray;
        Widgets.Label(new Rect(210f, rect.y + 4f, rect.width - 210f, 24f), busy ? "Rimlink.Economy.Working".Translate().ToString() : status);
        GUI.color = Color.white;

        var trades = social.Trades;
        var listRect = new Rect(0f, rect.y + 34f, 280f, rect.height - 34f);
        Widgets.BeginScrollView(listRect, ref listScroll, new Rect(0f, 0f, listRect.width - 16f, Math.Max(listRect.height, trades.Count * 52f)));
        float y = 0f;
        foreach (var t in trades)
        {
            var row = new Rect(0f, y, listRect.width - 16f, 48f);
            if (t.Id == selectedId) Widgets.DrawHighlightSelected(row);
            else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            Widgets.Label(new Rect(4f, y + 2f, row.width - 8f, 22f), PartnerOf(t)?.Name ?? "?");
            GUI.color = Color.gray;
            Widgets.Label(new Rect(4f, y + 24f, row.width - 8f, 22f), Summary(t));
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(row)) selectedId = t.Id;
            y += 52f;
        }
        if (trades.Count == 0) Widgets.Label(new Rect(0f, 0f, listRect.width - 16f, 48f), "Rimlink.Trades.None".Translate());
        Widgets.EndScrollView();

        var trade = trades.FirstOrDefault(t => t.Id == selectedId) ?? trades.FirstOrDefault();
        if (trade is not null) DrawCard(new Rect(292f, rect.y + 34f, rect.width - 292f, rect.height - 34f), trade, map);
    }

    private void DrawCard(Rect rect, Trade trade, Map? map)
    {
        selectedId = trade.Id;
        bool initiator = trade.Initiator?.PlayerId == RimlinkSession.MyPlayerId;
        var mine = (initiator ? trade.InitiatorSide : trade.PartnerSide) ?? new TradeSide();
        var theirs = (initiator ? trade.PartnerSide : trade.InitiatorSide) ?? new TradeSide();
        string partner = PartnerOf(trade)?.Name ?? "?";
        bool open = trade.Status == TradeStatus.TradeOpen;

        float buttons = open ? 34f : 0f;
        var body = new Rect(rect.x, rect.y, rect.width, rect.height - buttons);
        var lines = new List<string>
        {
            "Rimlink.Trades.CardTitle".Translate(partner, Summary(trade)),
            "",
            "Rimlink.Trades.MySide".Translate(Confirmed(mine)),
        };
        lines.AddRange(Describe(mine));
        lines.Add("");
        lines.Add("Rimlink.Trades.TheirSide".Translate(partner, Confirmed(theirs)));
        lines.AddRange(Describe(theirs));
        if (!open && trade.CloseReason.Length > 0) { lines.Add(""); lines.Add("Rimlink.Trades.Reason".Translate(trade.CloseReason)); }
        Widgets.BeginScrollView(body, ref cardScroll, new Rect(0f, 0f, body.width - 16f, Math.Max(body.height, lines.Count * 24f)));
        float y = 0f;
        foreach (string line in lines)
        {
            Widgets.Label(new Rect(0f, y, body.width - 16f, 24f), line);
            y += 24f;
        }
        Widgets.EndScrollView();

        if (!open) return;
        float by = rect.yMax - 30f;
        if (Widgets.ButtonText(new Rect(rect.x, by, 180f, 28f), "Rimlink.Trades.ChangeSide".Translate()))
        {
            if (map is null) status = "Rimlink.Trades.NeedHomeMap".Translate();
            else Find.WindowStack.Add(new Dialog_TradeSide(map, trade, partner));
        }
        if (!mine.Confirmed && Widgets.ButtonText(new Rect(rect.x + 186f, by, 140f, 28f), "Rimlink.Trades.Confirm".Translate()))
        {
            long id = trade.Id;
            int revision = trade.Revision;
            Call(c => c.Confirm(id, revision, CancellationToken.None), "Rimlink.Trades.Confirmed".Translate());
        }
        if (Widgets.ButtonText(new Rect(rect.xMax - 140f, by, 140f, 28f), "Rimlink.Trades.Cancel".Translate()))
        {
            long id = trade.Id;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.Trades.CancelConfirm".Translate(partner),
                () => Call(c => c.Cancel(id, CancellationToken.None), "Rimlink.Trades.Cancelled".Translate()), destructive: true));
        }
    }

    private void StartNew(Map? map, PlayerRef partner)
    {
        if (map is null) { status = "Rimlink.Trades.NeedHomeMap".Translate(); return; }
        Find.WindowStack.Add(new Dialog_TradeSide(map, null, partner.Name, partner.PlayerId));
    }

    private void Call(Func<Social.TradesClient, Task<Trade>> call, string success)
    {
        if (RimlinkSession.Trades is not { } client || busy) return;
        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var trade = await call(client);
                MainThread.Post(() => { RimlinkSession.State.Social.Upsert(trade); status = success; busy = false; });
            }
            catch (Exception ex)
            {
                // TRADE_CHANGED: the newer version came with the TradeUpdated event; the card already shows it.
                var refusal = Rimlink.Client.Net.RpcErrorDetail.Read(ex);
                MainThread.Post(() => { status = RefusalText.Of(refusal); busy = false; });
            }
        });
    }

    private static PlayerRef? PartnerOf(Trade t) => t.Initiator?.PlayerId == RimlinkSession.MyPlayerId ? t.Partner : t.Initiator;

    /// <summary>Status, whose turn, and the deadline of an open trade.</summary>
    private static string Summary(Trade t)
    {
        if (t.Status != TradeStatus.TradeOpen) return ("Rimlink.Trades.Status." + t.Status).Translate();
        bool initiator = t.Initiator?.PlayerId == RimlinkSession.MyPlayerId;
        var mine = initiator ? t.InitiatorSide : t.PartnerSide;
        string turn = (mine is { Confirmed: true } ? "Rimlink.Trades.WaitingForThem" : "Rimlink.Trades.WaitingForYou").Translate();
        string until = t.ExpiresAt?.ToDateTime().ToLocalTime().ToString("dd.MM HH:mm") ?? "?";
        return "Rimlink.Trades.OpenSummary".Translate(turn, until);
    }

    private static string Confirmed(TradeSide side) => side.Confirmed ? "✓" : "✗";

    private static IEnumerable<string> Describe(TradeSide side)
    {
        var lines = new List<string>();
        foreach (var i in side.Items?.Items ?? []) lines.Add("  " + (i.Count > 1 ? $"{i.Label} ×{i.Count}" : i.Label));
        foreach (var p in side.Pawns?.Pawns ?? []) lines.Add("  " + p.Label);
        if (side.Silver > 0) lines.Add("  " + "Rimlink.Economy.SilverAmount".Translate(side.Silver));
        if (side.Note.Length > 0) lines.Add("  " + "Rimlink.Trades.Note".Translate(side.Note));
        if (lines.Count == 0) lines.Add("  " + "Rimlink.Trades.Empty".Translate());
        return lines;
    }
}
