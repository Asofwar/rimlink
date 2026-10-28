using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Game.Visits;
using Rimlink.Client.UI;
using Rimlink.Client.UI.Visits;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game.Shop;

/// <summary>Prices the host put on things of this map; saved with the map and published after every upload.</summary>
public sealed class MapComponent_RimlinkShop(Map map) : MapComponent(map)
{
    private Dictionary<int, long> prices = [];

    public IReadOnlyDictionary<int, long> Prices => prices;

    public long PriceOf(Thing thing) => prices.GetValueOrDefault(thing.thingIDNumber);

    public void Set(Thing thing, long price)
    {
        if (price <= 0) prices.Remove(thing.thingIDNumber);
        else prices[thing.thingIDNumber] = price;
    }

    public void Forget(int thingId) => prices.Remove(thingId);

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref prices, "rimlinkShop", LookMode.Value, LookMode.Value);
        prices ??= [];
    }
}

/// <summary>Gizmos on things: "For sale" at home, "Buy" while visiting.</summary>
public static class ShopGizmos
{
    public static IEnumerable<Gizmo> Append(IEnumerable<Gizmo> gizmos, ThingWithComps thing)
    {
        foreach (var g in gizmos) yield return g;
        if (!RimlinkSession.Active || !thing.Spawned || thing.def.category != ThingCategory.Item || thing is Pawn) yield break;
        if (VisitSession.Active)
        {
            if (VisitSession.HostShop.TryGetValue(thing.thingIDNumber, out var item) && item.Count > 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Rimlink.Shop.BuyGizmo".Translate(item.Price),
                    defaultDesc = "Rimlink.Shop.BuyDesc".Translate(VisitSession.HostName, item.Price),
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/Trade", reportFailure: false),
                    action = () => Find.WindowStack.Add(new Dialog_Buy(thing, item)),
                };
            }
            yield break;
        }
        if (thing.Map is not { IsPlayerHome: true } map) yield break;
        var component = map.GetComponent<MapComponent_RimlinkShop>();
        if (component is null) yield break;
        long price = component.PriceOf(thing);
        yield return new Command_Action
        {
            defaultLabel = price > 0 ? "Rimlink.Shop.ForSaleGizmo".Translate(price) : "Rimlink.Shop.SellGizmo".Translate(),
            defaultDesc = "Rimlink.Shop.SellDesc".Translate(),
            icon = ContentFinder<Texture2D>.Get("UI/Commands/Trade", reportFailure: false),
            action = () => Find.WindowStack.Add(new Dialog_SetPrice(thing, component)),
        };
    }
}

/// <summary>Keeps the server's copy of the shop and the host's map in step.</summary>
public static class ShopSync
{
    /// <summary>
    /// The priced things as they are in the save being written (main thread, at save time). Published by the uploader
    /// right after the upload succeeds, while the connection is still open (a leave-to-menu save ends the session next).
    /// </summary>
    public static ShopList? Build(long saveTick)
    {
        if (!RimlinkSession.Active || VisitSession.Active) return null;
        var home = Find.AnyPlayerHomeMap;
        var component = home?.GetComponent<MapComponent_RimlinkShop>();
        if (home is null || component is null) return null;
        var list = new ShopList();
        foreach (var (id, price) in component.Prices.ToList())
        {
            var thing = home.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id && t.Spawned);
            if (thing is null) { component.Forget(id); continue; }
            list.Items.Add(new ShopItem
            {
                ThingId = id,
                DefName = thing.def.defName,
                StuffDef = thing.Stuff?.defName ?? "",
                Count = thing.stackCount,
                Quality = thing.TryGetQuality(out var q) ? (int)q : -1,
                HitPoints = thing.def.useHitPoints ? thing.HitPoints : 0,
                Price = price,
                SaveTick = saveTick,
                Label = thing.LabelCapNoCount,
            });
        }
        return list;
    }

    public static async Task Publish(ShopList list, Rimlink.Client.Net.RimlinkConnection connection)
    {
        try
        {
            await new Rimlink.Client.Visits.ShopClient(connection).Publish(list, CancellationToken.None);
            RimlinkLog.Instance.Info($"shop list published: {list.Items.Count} things");
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn("shop list not published: " + ex.Message);
        }
    }

    /// <summary>At login and map load: sold things leave the map, the removal is acknowledged through the outbox.</summary>
    public static void ApplyRemovals()
    {
        if (!RimlinkSession.Active || VisitSession.InVisit || RimlinkSession.Shop is not { } shop) return;
        _ = Task.Run(async () =>
        {
            RemovalList removals;
            try { removals = await shop.PendingRemovals(CancellationToken.None); }
            catch (Exception ex) { RimlinkLog.Instance.Warn("pending shop removals not fetched: " + ex.Message); return; }
            if (removals.Removals.Count == 0) return;
            MainThread.Post(() => Remove(removals, shop));
        });
    }

    private static void Remove(RemovalList removals, Rimlink.Client.Visits.ShopClient shop)
    {
        if (!RimlinkSession.Active || VisitSession.InVisit || Current.ProgramState != ProgramState.Playing || Find.AnyPlayerHomeMap is null) return;
        var ack = new RemovalAck();
        foreach (var removal in removals.Removals)
        {
            foreach (var map in Find.Maps.Where(m => m.IsPlayerHome))
            {
                var thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == (int)removal.ThingId && t.Spawned);
                if (thing is null) continue;
                int count = Math.Min(removal.Count, thing.stackCount);
                if (count >= thing.stackCount) thing.Destroy(DestroyMode.Vanish);
                else thing.SplitOff(count).Destroy(DestroyMode.Vanish);
                map.GetComponent<MapComponent_RimlinkShop>()?.Forget((int)removal.ThingId);
                break;
            }
            ack.Ids.Add(removal.Id); // a thing that is already gone needs no further removal
        }
        ack.GameTick = Find.TickManager.TicksGame;
        Messages.Message("Rimlink.Shop.Removed".Translate(removals.Removals.Count), MessageTypeDefOf.NeutralEvent, historical: false);
        var outbox = RimlinkSession.Outbox;
        var entry = outbox?.Add(ack);
        SaveSync.TriggerSave("shop-removal");
        _ = Task.Run(async () =>
        {
            try
            {
                await shop.AckRemoval(ack, CancellationToken.None);
                if (entry is not null) outbox!.Remove(entry.Id);
            }
            catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
            {
                if (entry is not null) outbox!.Remove(entry.Id);
                RimlinkLog.Instance.Warn("shop removal ack refused: " + EconomyClient.MessageOf(ex));
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn("shop removal ack unanswered, kept in the outbox: " + ex.Message);
            }
        });
    }
}
