using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Game.Shop;
using Rimlink.Client.Game.Visits;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Visits;

/// <summary>Price per unit for a thing at home; 0 takes it off the shelf.</summary>
public sealed class Dialog_SetPrice : Window
{
    private readonly Thing thing;
    private readonly MapComponent_RimlinkShop shop;
    private int price;
    private string buffer;

    public override Vector2 InitialSize => new(360f, 180f);

    public Dialog_SetPrice(Thing thing, MapComponent_RimlinkShop shop)
    {
        this.thing = thing;
        this.shop = shop;
        price = (int)Math.Min(int.MaxValue, shop.PriceOf(thing));
        buffer = price.ToString();
        doCloseX = true;
        forcePause = true;
        closeOnClickedOutside = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Widgets.Label(new Rect(0f, 0f, inRect.width, 48f), "Rimlink.Shop.SetPrice".Translate(thing.LabelCapNoCount, thing.stackCount));
        Widgets.Label(new Rect(0f, 56f, 140f, 26f), "Rimlink.Shop.PriceEach".Translate());
        Widgets.TextFieldNumeric(new Rect(150f, 56f, 120f, 26f), ref price, ref buffer, 0, 1_000_000);
        if (Widgets.ButtonText(new Rect(0f, inRect.height - 34f, 150f, 30f), "Rimlink.Shop.Apply".Translate()))
        {
            shop.Set(thing, price);
            Messages.Message(price > 0 ? "Rimlink.Shop.Listed".Translate(thing.LabelCapNoCount, price) : "Rimlink.Shop.Unlisted".Translate(thing.LabelCapNoCount), MessageTypeDefOf.NeutralEvent, historical: false);
            Close();
        }
        if (Widgets.ButtonText(new Rect(inRect.width - 150f, inRect.height - 34f, 150f, 30f), "Rimlink.Raid.Cancel".Translate())) Close();
    }
}

/// <summary>How many to buy from the host's shelf; the copy stays whole, the purchase arrives at home as a parcel.</summary>
public sealed class Dialog_Buy : Window
{
    private readonly Thing thing;
    private readonly ShopItem item;
    private int count = 1;
    private string buffer = "1";

    public override Vector2 InitialSize => new(380f, 200f);

    public Dialog_Buy(Thing thing, ShopItem item)
    {
        this.thing = thing;
        this.item = item;
        doCloseX = true;
        forcePause = true;
        closeOnClickedOutside = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        int max = Math.Max(1, Math.Min(item.Count, thing.stackCount));
        Widgets.Label(new Rect(0f, 0f, inRect.width, 48f), "Rimlink.Shop.BuyIntro".Translate(thing.LabelCapNoCount, VisitSession.HostName, item.Price, max));
        Widgets.Label(new Rect(0f, 56f, 140f, 26f), "Rimlink.Shop.Count".Translate());
        Widgets.TextFieldNumeric(new Rect(150f, 56f, 120f, 26f), ref count, ref buffer, 1, max);
        Widgets.Label(new Rect(0f, 88f, inRect.width, 26f), "Rimlink.Shop.Total".Translate(item.Price * count, RimlinkSession.State.Economy.Balance));
        if (Widgets.ButtonText(new Rect(0f, inRect.height - 34f, 150f, 30f), "Rimlink.Shop.Buy".Translate()))
        {
            Buy(Math.Clamp(count, 1, max));
            Close();
        }
        if (Widgets.ButtonText(new Rect(inRect.width - 150f, inRect.height - 34f, 150f, 30f), "Rimlink.Raid.Cancel".Translate())) Close();
    }

    private void Buy(int n)
    {
        if (RimlinkSession.Shop is not { } shop) return;
        ItemBlob blob;
        try
        {
            var part = n < thing.stackCount ? thing.SplitOff(n) : thing;
            blob = ThingBlobs.SerializeItem(part);
            if (part != thing) thing.TryAbsorbStack(part, respectStackLimit: false);
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not serialise the purchase: " + ex);
            Messages.Message("Rimlink.Shop.Refused".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        var request = new BuyRequest { ClientRequestId = EconomyClient.NewRequestId(), HostPlayerId = VisitSession.HostId, ThingId = item.ThingId, Count = n, Blob = blob };
        var outbox = RimlinkSession.Outbox;
        var entry = outbox?.Add(request);
        long price = item.Price * n;
        _ = Task.Run(async () =>
        {
            try
            {
                await shop.Buy(request, CancellationToken.None);
                if (entry is not null) outbox!.Remove(entry.Id);
                MainThread.Post(() =>
                {
                    item.Count -= n;
                    Messages.Message("Rimlink.Shop.Bought".Translate(n, thing.LabelCapNoCount, price), MessageTypeDefOf.PositiveEvent, historical: false);
                });
            }
            catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
            {
                string reason = EconomyClient.MessageOf(ex);
                if (entry is not null) outbox!.Remove(entry.Id);
                MainThread.Post(() => Messages.Message("Rimlink.Shop.Refused".Translate(reason), MessageTypeDefOf.RejectInput, historical: false));
            }
            catch (Exception ex)
            {
                string reason = ex is RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                MainThread.Post(() => Messages.Message("Rimlink.Economy.Unanswered".Translate(reason), MessageTypeDefOf.NegativeEvent, historical: false));
            }
        });
    }
}
