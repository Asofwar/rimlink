using Grpc.Core;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Economy;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// Everything that leaves the colony: things and pawns are taken out of the game and serialised, the game is saved and
/// uploaded (no upload, no transfer: the server refuses older saves afterwards), then the RPC runs. On any failure the
/// objects come back exactly where they were. Main thread entry points; the RPC runs in the background.
/// </summary>
public static class Outgoing
{
    /// <summary>A serialised object and the way to put it back.</summary>
    private sealed record Taken(ItemBlob? Item, PawnBlob? Pawn, Action<Thing> PutBack);

    public sealed record Pick(Thing Thing, int Count);

    public static void SendItems(Map map, long toPlayerId, string toName, IReadOnlyList<Pick> picks, IReadOnlyList<Pawn> pawns, long silver)
    {
        if (CheckLimits(picks.Count, pawns.Count) is { } limit) { Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false); return; }
        var taken = TakeFromMap(map, picks, pawns);
        if (taken is null) return;
        Run(taken, "Rimlink.Economy.Sent".Translate(toName), (items, pawnList, tick) =>
            RimlinkSession.Economy!.Send(new TransferRequest
            {
                ClientRequestId = EconomyClient.NewRequestId(), ToPlayerId = toPlayerId, Items = items, Pawns = pawnList, Silver = silver, GameTick = tick,
            }, CancellationToken.None));
    }

    public static void PostLot(Map map, IReadOnlyList<Pick> picks, Pawn? pawn, long price)
    {
        IReadOnlyList<Pawn> pawns = pawn is null ? [] : [pawn];
        if (CheckLimits(picks.Count, pawns.Count) is { } limit) { Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false); return; }
        var taken = TakeFromMap(map, picks, pawns);
        if (taken is null) return;
        Run(taken, "Rimlink.Economy.Posted".Translate(price), async (items, pawnList, tick) =>
        {
            var request = new PostRequest { ClientRequestId = EconomyClient.NewRequestId(), Price = price, GameTick = tick };
            if (pawnList.Pawns.Count > 0) request.Pawn = pawnList.Pawns[0]; else request.Items = items;
            var lot = await RimlinkSession.Economy!.Post(request, CancellationToken.None);
            MainThread.Post(() => RimlinkSession.State.Economy.MyLots.Add(lot));
        });
    }

    /// <summary>Silver under the beacons goes into the wallet.</summary>
    public static void Deposit(Map map, int silver)
    {
        var picks = new List<Pick>();
        int left = silver;
        var source = BlobDebugActions.PickAnywhere && Prefs.DevMode
            ? map.listerThings.ThingsOfDef(ThingDefOf.Silver).Where(t => t.Spawned)
            : TradeUtility.AllLaunchableThingsForTrade(map);
        foreach (var stack in source.Where(t => t.def == ThingDefOf.Silver))
        {
            if (left <= 0) break;
            int take = Math.Min(left, stack.stackCount);
            picks.Add(new Pick(stack, take));
            left -= take;
        }
        if (left > 0) { Messages.Message("Rimlink.Economy.NotEnoughSilverOnMap".Translate(silver), MessageTypeDefOf.RejectInput, historical: false); return; }
        var taken = TakeFromMap(map, picks, []);
        if (taken is null) return;
        Run(taken, "Rimlink.Economy.Deposited".Translate(silver), async (_, _, tick) =>
        {
            var state = await RimlinkSession.Economy!.Deposit(new DepositRequest { ClientRequestId = EconomyClient.NewRequestId(), Silver = silver, GameTick = tick }, CancellationToken.None);
            MainThread.Post(() => RimlinkSession.State.Economy.Apply(new ServerEvent { WalletChanged = new WalletChanged { Balance = state.Balance, Delta = silver, Kind = "deposit" } }));
        });
    }

    /// <summary>From a caravan standing on another player's settlement: inventory items and members.</summary>
    public static void SendFromCaravan(Caravan caravan, long toPlayerId, string toName, IReadOnlyList<Thing> things, IReadOnlyList<Pawn> pawns)
    {
        if (CheckLimits(things.Count, pawns.Count) is { } limit) { Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false); return; }
        var taken = new List<Taken>();
        try
        {
            foreach (var thing in things)
            {
                var owner = CaravanInventoryUtility.GetOwnerOf(caravan, thing);
                owner?.inventory.innerContainer.Remove(thing);
                taken.Add(new Taken(ThingBlobs.SerializeItem(thing), null, t => (CaravanInventoryUtility.FindPawnToMoveInventoryTo(t, caravan.PawnsListForReading, null) ?? caravan.PawnsListForReading[0]).inventory.innerContainer.TryAdd(t)));
                thing.Destroy(DestroyMode.Vanish);
            }
            foreach (var pawn in pawns)
            {
                caravan.RemovePawn(pawn);
                taken.Add(new Taken(null, ThingBlobs.SerializePawn(pawn), p => caravan.AddPawn((Pawn)p, addCarriedPawnToWorldPawnsIfAny: true)));
                pawn.Destroy(DestroyMode.Vanish);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not take things from the caravan: " + ex);
            Restore(taken);
            Messages.Message("Rimlink.Economy.Failed".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Run(taken, "Rimlink.Economy.Sent".Translate(toName), async (items, pawnList, tick) =>
        {
            await RimlinkSession.Economy!.Send(new TransferRequest
            {
                ClientRequestId = EconomyClient.NewRequestId(), ToPlayerId = toPlayerId, Items = items, Pawns = pawnList, Silver = 0, GameTick = tick,
            }, CancellationToken.None);
            MainThread.Post(() => { if (!caravan.Destroyed && caravan.PawnsListForReading.Count == 0) caravan.Destroy(); });
        });
    }

    public static string? CheckLimits(int items, int pawns)
    {
        if (items > Limits.EconomyMaxItemsPerParcel) return "Rimlink.Economy.TooManyItems".Translate(Limits.EconomyMaxItemsPerParcel);
        if (pawns > Limits.EconomyMaxPawnsPerParcel) return "Rimlink.Economy.TooManyPawns".Translate(Limits.EconomyMaxPawnsPerParcel);
        return null;
    }

    private static List<Taken>? TakeFromMap(Map map, IReadOnlyList<Pick> picks, IReadOnlyList<Pawn> pawns)
    {
        var taken = new List<Taken>();
        try
        {
            foreach (var pick in picks)
            {
                var cell = pick.Thing.Position;
                var thing = pick.Count < pick.Thing.stackCount ? pick.Thing.SplitOff(pick.Count) : pick.Thing;
                if (thing.Spawned) thing.DeSpawn();
                taken.Add(new Taken(ThingBlobs.SerializeItem(thing), null, t => GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near)));
                thing.Destroy(DestroyMode.Vanish);
            }
            foreach (var pawn in pawns)
            {
                var cell = pawn.Position;
                if (pawn.Spawned) pawn.DeSpawn();
                taken.Add(new Taken(null, ThingBlobs.SerializePawn(pawn), p => GenSpawn.Spawn(p, cell, map)));
                pawn.Destroy(DestroyMode.Vanish);
            }
            return taken;
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not take things off the map: " + ex);
            Restore(taken);
            Messages.Message("Rimlink.Economy.Failed".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
            return null;
        }
    }

    private static void Run(List<Taken> taken, string successMessage, Func<ItemList, PawnList, long, Task> call)
    {
        if (!RimlinkSession.Active || RimlinkSession.Economy is null) { Restore(taken); return; }
        var items = new ItemList();
        var pawnList = new PawnList();
        foreach (var t in taken)
        {
            if (t.Item is not null) items.Items.Add(t.Item);
            if (t.Pawn is not null) pawnList.Pawns.Add(t.Pawn);
        }
        if (ThingBlobs.SizeOf(items, pawnList) > Limits.EconomyMaxParcelBytes)
        {
            Restore(taken);
            Messages.Message("Rimlink.Economy.TooBig".Translate(Limits.EconomyMaxParcelBytes / 1024 / 1024), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        // The save that no longer holds these things must reach the server before they can exist anywhere else.
        SaveSync.TriggerSave("transfer");
        LongEventHandler.QueueLongEvent(() => SaveSync.WaitForUpload(TimeSpan.FromSeconds(60)), "Rimlink.Flow.Uploading", doAsynchronously: true, null, showExtraUIInfo: false, callback: () =>
        {
            if (SaveSync.LastError is not null)
            {
                Restore(taken);
                Messages.Message("Rimlink.Economy.UploadFailed".Translate(SaveSync.LastError), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            long tick = Find.TickManager.TicksGame;
            _ = Task.Run(async () =>
            {
                try
                {
                    await call(items, pawnList, tick);
                    MainThread.Post(() => Messages.Message(successMessage, MessageTypeDefOf.PositiveEvent, historical: false));
                }
                catch (Exception ex)
                {
                    string reason = ex is RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                    RimlinkLog.Instance.Warn($"transfer refused: {reason}");
                    MainThread.Post(() =>
                    {
                        Restore(taken);
                        Messages.Message("Rimlink.Economy.Failed".Translate(reason), MessageTypeDefOf.RejectInput, historical: false);
                    });
                }
            });
        });
    }

    /// <summary>Puts everything back where it was taken from (main thread).</summary>
    private static void Restore(List<Taken> taken)
    {
        foreach (var t in taken)
        {
            try
            {
                if (t.Item is not null) t.PutBack(ThingBlobs.DeserializeItem(t.Item));
                else if (t.Pawn is not null) t.PutBack(ThingBlobs.DeserializePawn(t.Pawn));
            }
            catch (Exception ex)
            {
                Log.Error("[Rimlink] could not restore a transferred object: " + ex);
            }
        }
        taken.Clear();
    }
}
