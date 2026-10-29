using RimWorld;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Economy;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Economy;

/// <summary>
/// Opens a trade (no trade yet) or changes our side of one: things and pawns from under the beacons as when sending, silver
/// from the wallet and a note. A change adds the picked things to what is already offered unless "take back what is offered"
/// is ticked; silver and note are the side's new totals.
/// </summary>
public sealed class Dialog_TradeSide : Window
{
    private readonly Map map;
    private readonly Trade? trade;
    private readonly string partnerName;
    private readonly long partnerId;
    private readonly ThingPicker picker;
    private readonly long walletAndOffered;
    private int silver;
    private string silverBuffer;
    private string note;
    private bool returnGoods;

    public Dialog_TradeSide(Map map, Trade? trade, string partnerName, long partnerId = 0)
    {
        this.map = map;
        this.trade = trade;
        this.partnerName = partnerName;
        this.partnerId = partnerId;
        picker = new ThingPicker(map, allowPawns: true);
        var mine = trade is null ? null : trade.Initiator?.PlayerId == RimlinkSession.MyPlayerId ? trade.InitiatorSide : trade.PartnerSide;
        silver = (int)Math.Min(int.MaxValue, mine?.Silver ?? 0);
        silverBuffer = silver.ToString();
        note = mine?.Note ?? "";
        // Silver already in the trade counts: the new total may use it plus the wallet.
        walletAndOffered = RimlinkSession.State.Economy.Balance + (mine?.Silver ?? 0);
        doCloseX = true;
        draggable = true;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
    }

    public override Vector2 InitialSize => new(700f, 560f);

    public override void DoWindowContents(Rect inRect)
    {
        if (!RimlinkSession.Active) { Close(); return; }
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), (trade is null ? "Rimlink.Trades.NewTitle" : "Rimlink.Trades.ChangeTitle").Translate(partnerName));
        Text.Font = GameFont.Small;

        float bottom = trade is null ? 100f : 130f;
        picker.Draw(new Rect(0f, 38f, inRect.width, inRect.height - 38f - bottom));
        float y = inRect.height - bottom + 6f;
        Widgets.Label(new Rect(0f, y + 3f, 220f, 26f), "Rimlink.Trades.SilverTotal".Translate(walletAndOffered));
        Widgets.TextFieldNumeric(new Rect(224f, y, 120f, 26f), ref silver, ref silverBuffer, 0, (int)Math.Min(int.MaxValue, walletAndOffered));
        y += 30f;
        Widgets.Label(new Rect(0f, y + 3f, 120f, 26f), "Rimlink.Trades.NoteLabel".Translate());
        note = Widgets.TextField(new Rect(124f, y, inRect.width - 124f, 26f), note);
        if (note.Length > 200) note = note.Substring(0, 200);
        y += 30f;
        if (trade is not null)
        {
            Widgets.CheckboxLabeled(new Rect(0f, y, 420f, 26f), "Rimlink.Trades.ReturnGoods".Translate(), ref returnGoods);
            y += 30f;
        }

        if (Widgets.ButtonText(new Rect(inRect.width - 200f, inRect.height - 30f, 200f, 28f), (trade is null ? "Rimlink.Trades.Offer" : "Rimlink.Trades.Apply").Translate()))
        {
            var picks = picker.Picks;
            var pawns = picker.Pawns;
            string text = note.Trim();
            long amount = silver;
            if (trade is null)
            {
                if (RimlinkSession.State.Social.IsBlocked(partnerId)) { Messages.Message("Rimlink.Social.YouBlocked".Translate(partnerName), MessageTypeDefOf.RejectInput, historical: false); return; }
                Confirm(pawns, () => { Outgoing.OpenTrade(map, partnerId, partnerName, picks, pawns, amount, text); Close(); });
            }
            else
            {
                var current = trade;
                bool back = returnGoods;
                Confirm(pawns, () => { Outgoing.SetTradeSide(map, current, picks, pawns, amount, text, back); Close(); });
            }
        }
    }

    private static void Confirm(IReadOnlyList<Pawn> pawns, Action go)
    {
        if (pawns.Any(p => p.RaceProps.Humanlike))
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.Trades.ConfirmPawn".Translate(string.Join(", ", pawns.Select(p => p.LabelShort))), go, destructive: true));
        else go();
    }
}
