using RimWorld;
using Rimlink.Client.UI.Economy;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>Tells the seller about a sale: a market lot bought, or a thing bought from the shop during a visit.</summary>
public static class SaleNotices
{
    private static bool subscribed;

    /// <summary>Once per process: the shared state lives as long as the game, and its events fire on the main thread.</summary>
    public static void Subscribe()
    {
        if (subscribed) return;
        subscribed = true;
        RimlinkSession.State.Economy.LotSold += OnLotSold;
        RimlinkSession.State.Raids.ItemSold += OnItemSold;
    }

    private static void OnLotSold(LotSold sold, Lot? lot)
    {
        string what = lot is not null ? Dialog_Economy.Describe(lot) : "Rimlink.Economy.LotNumber".Translate(sold.LotId).ToString();
        RimlinkLog.Instance.Info($"lot {sold.LotId} sold to {sold.Buyer?.Name} for {sold.Price}");
        Messages.Message("Rimlink.Economy.LotSold".Translate(what, sold.Buyer?.Name ?? "?", sold.Price, sold.Proceeds), MessageTypeDefOf.PositiveEvent);
    }

    /// <summary>The thing is still on the home map until the removal is applied at the next load, so its label is read from there.</summary>
    private static void OnItemSold(ItemSold sold)
    {
        RimlinkLog.Instance.Info($"shop thing {sold.ThingId} ×{sold.Count} sold to {sold.Buyer?.Name} for {sold.Price}");
        string buyer = sold.Buyer?.Name ?? "?";
        var thing = Visits.VisitSession.InVisit ? null : Find.Maps.Where(m => m.IsPlayerHome)
            .Select(m => m.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == (int)sold.ThingId && t.Spawned))
            .FirstOrDefault(t => t is not null);
        string text = thing is not null
            ? "Rimlink.Shop.Sold".Translate(buyer, sold.Count, thing.LabelCapNoCount, sold.Price)
            : "Rimlink.Shop.SoldUnknown".Translate(buyer, sold.Count, sold.Price);
        Messages.Message(text, MessageTypeDefOf.PositiveEvent);
    }
}
