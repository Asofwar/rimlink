using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.UI;
using Rimlink.Client.UI.Economy;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// A player's gift waiting for a decision: "accept" lets it drop like any parcel, "refuse" sends it back to its sender.
/// Postponed, it stays in the letter stack and in the mailbox tab until answered or refused by the server's sweep.
/// </summary>
public class ChoiceLetter_RimlinkGift : ChoiceLetter
{
    public long parcelId;

    public override bool CanDismissWithRightClick => false;

    /// <summary>Gone once the gift is no longer waiting (answered here, in the mailbox tab, or refused by the sweep).</summary>
    public override bool CanShowInLetterStack =>
        base.CanShowInLetterStack && RimlinkSession.State.Economy.Pending.Any(p => p.Id == parcelId && p.AwaitingDecision);

    public override IEnumerable<DiaOption> Choices
    {
        get
        {
            yield return Answer(accept: true);
            yield return Answer(accept: false);
            yield return Option_Postpone;
        }
    }

    private DiaOption Answer(bool accept) => new((accept ? "Rimlink.Gifts.Accept" : "Rimlink.Gifts.Refuse").Translate())
    {
        action = () =>
        {
            GiftLetters.Answer(parcelId, accept);
            Find.LetterStack.RemoveLetter(this);
        },
        resolveTree = true,
    };

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref parcelId, "rimlinkParcelId");
    }
}

/// <summary>Letters about gifts: an offer to the recipient, a refusal to the sender. Main thread (state events fire there).</summary>
public static class GiftLetters
{
    private static bool subscribed;

    public static void Subscribe()
    {
        if (subscribed) return;
        subscribed = true;
        RimlinkSession.State.Economy.ParcelArrived += OnArrived;
        RimlinkSession.State.Social.GiftRefused += OnRefused;
    }

    private static void OnArrived(Parcel parcel)
    {
        if (!parcel.AwaitingDecision || Current.ProgramState != ProgramState.Playing) return;
        var letter = (ChoiceLetter_RimlinkGift)LetterMaker.MakeLetter(
            "Rimlink.Gifts.OfferLabel".Translate(parcel.From?.Name ?? "?"),
            "Rimlink.Gifts.OfferText".Translate(parcel.From?.Name ?? "?", Dialog_Economy.Describe(parcel)),
            DefDatabase<LetterDef>.GetNamed("Rimlink_GiftOffer"));
        letter.parcelId = parcel.Id;
        Find.LetterStack.ReceiveLetter(letter);
    }

    private static void OnRefused(GiftRefused refused)
    {
        if (refused.By?.PlayerId == RimlinkSession.MyPlayerId) return; // our own refusal (or the sweep's): the parcel already left the mailbox
        if (Current.ProgramState != ProgramState.Playing) return;
        string who = refused.By?.Name ?? "?";
        Find.LetterStack.ReceiveLetter(
            "Rimlink.Gifts.RefusedLabel".Translate(who),
            (refused.Expired ? "Rimlink.Gifts.ExpiredText" : "Rimlink.Gifts.RefusedText").Translate(who, refused.Returned),
            LetterDefOf.NeutralEvent);
    }

    /// <summary>Sends the answer; the server re-sends the parcel without the flag (accept) or a GiftRefused (refuse).</summary>
    public static void Answer(long parcelId, bool accept)
    {
        if (RimlinkSession.Economy is not { } economy) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await economy.Answer(parcelId, accept, CancellationToken.None);
                MainThread.Post(() => Messages.Message((accept ? "Rimlink.Gifts.Accepted" : "Rimlink.Gifts.Refused").Translate(), MessageTypeDefOf.NeutralEvent, historical: false));
            }
            catch (Exception ex)
            {
                string reason = ex is RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                RimlinkLog.Instance.Warn($"gift {parcelId} answer not delivered: {reason}");
                MainThread.Post(() => Messages.Message("Rimlink.Economy.Failed".Translate(reason), MessageTypeDefOf.RejectInput, historical: false));
            }
        });
    }
}

/// <summary>Short notices when a trade changes; the trades tab has the details.</summary>
public static class TradeNotices
{
    private static bool subscribed;

    public static void Subscribe()
    {
        if (subscribed) return;
        subscribed = true;
        RimlinkSession.State.Social.TradeUpdated += OnUpdated;
    }

    private static void OnUpdated(Trade trade)
    {
        if (Current.ProgramState != ProgramState.Playing) return;
        long me = RimlinkSession.MyPlayerId;
        bool initiator = trade.Initiator?.PlayerId == me;
        string partner = (initiator ? trade.Partner?.Name : trade.Initiator?.Name) ?? "?";
        var mine = initiator ? trade.InitiatorSide : trade.PartnerSide;
        switch (trade.Status)
        {
            case TradeStatus.TradeCompleted:
                Messages.Message("Rimlink.Trades.CompletedNotice".Translate(partner), MessageTypeDefOf.PositiveEvent);
                break;
            case TradeStatus.TradeCancelled:
            case TradeStatus.TradeExpired:
                Messages.Message("Rimlink.Trades.ClosedNotice".Translate(partner, trade.CloseReason), MessageTypeDefOf.NeutralEvent);
                break;
            default:
                // Our side unconfirmed means the other side offered or changed something: our turn.
                if (mine is { Confirmed: false }) Messages.Message("Rimlink.Trades.YourTurnNotice".Translate(partner), MessageTypeDefOf.NeutralEvent);
                break;
        }
    }
}
