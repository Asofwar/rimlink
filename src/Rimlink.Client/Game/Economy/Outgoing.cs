using Google.Protobuf;
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
/// uploaded (no upload, no transfer: the server refuses older saves afterwards), then the RPC runs. A refusal puts the
/// objects back exactly where they were. A call that got no answer is neither restored nor forgotten: it waits in the
/// <see cref="TransferOutbox"/> and is replayed with the same key at the next login or reconnect. Main thread entry
/// points; the RPC runs in the background.
/// </summary>
public static class Outgoing
{
    /// <summary>A serialised object and the way to put it back.</summary>
    private sealed record Taken(ItemBlob? Item, PawnBlob? Pawn, Action<Thing> PutBack);

    public sealed record Pick(Thing Thing, int Count);

    private static DateTime lastReturn = DateTime.MinValue;

    public static void SendItems(Map map, long toPlayerId, string toName, IReadOnlyList<Pick> picks, IReadOnlyList<Pawn> pawns, long silver)
    {
        if (CheckLimits(picks.Count, pawns.Count) is { } limit) { Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false); return; }
        var taken = TakeFromMap(map, picks, pawns);
        if (taken is null) return;
        Run(taken, "Rimlink.Economy.Sent".Translate(toName),
            (items, pawnList, tick) => new TransferRequest { ClientRequestId = EconomyClient.NewRequestId(), ToPlayerId = toPlayerId, Items = items, Pawns = pawnList, Silver = silver, GameTick = tick },
            request => RimlinkSession.Economy!.Send((TransferRequest)request, CancellationToken.None));
    }

    public static void PostLot(Map map, IReadOnlyList<Pick> picks, Pawn? pawn, long price)
    {
        IReadOnlyList<Pawn> pawns = pawn is null ? [] : [pawn];
        if (CheckLimits(picks.Count, pawns.Count) is { } limit) { Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false); return; }
        var taken = TakeFromMap(map, picks, pawns);
        if (taken is null) return;
        Run(taken, "Rimlink.Economy.Posted".Translate(price),
            (items, pawnList, tick) =>
            {
                var request = new PostRequest { ClientRequestId = EconomyClient.NewRequestId(), Price = price, GameTick = tick };
                if (pawnList.Pawns.Count > 0) request.Pawn = pawnList.Pawns[0]; else request.Items = items;
                return request;
            },
            async request =>
            {
                var lot = await RimlinkSession.Economy!.Post((PostRequest)request, CancellationToken.None);
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
        Run(taken, "Rimlink.Economy.Deposited".Translate(silver),
            (_, _, tick) => new DepositRequest { ClientRequestId = EconomyClient.NewRequestId(), Silver = silver, GameTick = tick },
            async request =>
            {
                var state = await RimlinkSession.Economy!.Deposit((DepositRequest)request, CancellationToken.None);
                MainThread.Post(() => RimlinkSession.State.Economy.Apply(new ServerEvent { WalletChanged = new WalletChanged { Balance = state.Balance, Delta = silver, Kind = "deposit" } }));
            });
    }

    /// <summary>From a caravan standing on another player's settlement: inventory items and members. A colonist must stay to carry what remains.</summary>
    public static void SendFromCaravan(Caravan caravan, long toPlayerId, string toName, IReadOnlyList<Thing> things, IReadOnlyList<Pawn> pawns)
    {
        if (CheckLimits(things.Count, pawns.Count) is { } limit) { Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false); return; }
        if (pawns.Count > 0 && !caravan.PawnsListForReading.Except(pawns).Any(caravan.IsOwner))
        {
            Messages.Message("Rimlink.Economy.KeepOneColonist".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        var taken = new List<Taken>();
        try
        {
            // Pawns first: when things are put back, their carriers are already back in the caravan.
            foreach (var pawn in pawns)
            {
                caravan.RemovePawn(pawn);
                taken.Add(new Taken(null, ThingBlobs.SerializePawn(pawn), p => caravan.AddPawn((Pawn)p, addCarriedPawnToWorldPawnsIfAny: true)));
                pawn.Destroy(DestroyMode.Vanish);
            }
            foreach (var thing in things)
            {
                var owner = CaravanInventoryUtility.GetOwnerOf(caravan, thing);
                owner?.inventory.innerContainer.Remove(thing);
                taken.Add(new Taken(ThingBlobs.SerializeItem(thing), null, t => (CaravanInventoryUtility.FindPawnToMoveInventoryTo(t, caravan.PawnsListForReading, null) ?? caravan.PawnsListForReading[0]).inventory.innerContainer.TryAdd(t)));
                thing.Destroy(DestroyMode.Vanish);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not take things from the caravan: " + ex);
            Restore(taken);
            Messages.Message("Rimlink.Economy.Failed".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Run(taken, "Rimlink.Economy.Sent".Translate(toName),
            (items, pawnList, tick) => new TransferRequest { ClientRequestId = EconomyClient.NewRequestId(), ToPlayerId = toPlayerId, Items = items, Pawns = pawnList, Silver = 0, GameTick = tick },
            request => RimlinkSession.Economy!.Send((TransferRequest)request, CancellationToken.None));
    }

    /// <summary>Raids: pawns (and what they wear and carry) leave a caravan standing on the target's tile.</summary>
    public static void RunWithCaravanPawns(Caravan caravan, IReadOnlyList<Pawn> pawns, string successMessage, Func<PawnList, long, IMessage> build, Func<IMessage, Task> call)
    {
        var taken = new List<Taken>();
        try
        {
            foreach (var pawn in pawns)
            {
                caravan.RemovePawn(pawn);
                taken.Add(new Taken(null, ThingBlobs.SerializePawn(pawn), p => caravan.AddPawn((Pawn)p, addCarriedPawnToWorldPawnsIfAny: true)));
                pawn.Destroy(DestroyMode.Vanish);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not take pawns from the caravan: " + ex);
            Restore(taken);
            Messages.Message("Rimlink.Economy.Failed".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Run(taken, successMessage, (_, pawnList, tick) => build(pawnList, tick), call);
    }

    /// <summary>Pawns taken straight off the map (dev harness and future uses); a refusal puts them back where they stood.</summary>
    public static void RunWithMapPawns(Map map, IReadOnlyList<Pawn> pawns, string successMessage, Func<PawnList, long, IMessage> build, Func<IMessage, Task> call)
    {
        var taken = TakeFromMap(map, [], pawns);
        if (taken is null) return;
        Run(taken, successMessage, (_, pawnList, tick) => build(pawnList, tick), call);
    }

    /// <summary>Raids by pod: the pawns are already out of their pods (unspawned); a refusal sends them home by pod.</summary>
    public static void RunWithPodPawns(IReadOnlyList<Pawn> pawns, string successMessage, Func<PawnList, long, IMessage> build, Func<IMessage, Task> call)
    {
        var taken = new List<Taken>();
        try
        {
            foreach (var pawn in pawns)
            {
                taken.Add(new Taken(null, ThingBlobs.SerializePawn(pawn), p =>
                {
                    var home = Find.AnyPlayerHomeMap;
                    if (home is not null) DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(home), home, [p], openDelay: 110, canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: false, forbid: false);
                }));
                pawn.Destroy(DestroyMode.Vanish);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not take pawns from the pods: " + ex);
            Restore(taken);
            Messages.Message("Rimlink.Economy.Failed".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Run(taken, successMessage, (_, pawnList, tick) => build(pawnList, tick), call);
    }

    public static string? CheckLimits(int items, int pawns)
    {
        if (items > Limits.EconomyMaxItemsPerParcel) return "Rimlink.Economy.TooManyItems".Translate(Limits.EconomyMaxItemsPerParcel);
        if (pawns > Limits.EconomyMaxPawnsPerParcel) return "Rimlink.Economy.TooManyPawns".Translate(Limits.EconomyMaxPawnsPerParcel);
        return null;
    }

    /// <summary>
    /// Called every frame on the colony map: a request the server refused on replay (its contents were already gone
    /// from the colony when the refusal came) comes back by drop pod.
    /// </summary>
    public static void ReturnRefused(Map map)
    {
        if (DateTime.UtcNow - lastReturn < TimeSpan.FromSeconds(5)) return;
        lastReturn = DateTime.UtcNow;
        var outbox = RimlinkSession.Outbox;
        if (outbox is null) return;
        foreach (var entry in outbox.Entries.Where(e => e.Refusal is not null))
        {
            List<Thing> things;
            try
            {
                things = Contents(TransferOutbox.Decode(entry));
            }
            catch (Exception ex)
            {
                Log.Error($"[Rimlink] refused transfer {entry.Id} could not be unpacked, its entry stays in the outbox: {ex}");
                lastReturn = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                return;
            }
            var center = Incoming.DropCenter(map);
            DropPodUtility.DropThingsNear(center, map, things, openDelay: 110, canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: true, forbid: false);
            outbox.Remove(entry.Id);
            Messages.Message("Rimlink.Economy.Returned".Translate(entry.Refusal), new LookTargets(center, map), MessageTypeDefOf.NegativeEvent, historical: false);
            RimlinkLog.Instance.Info($"refused {entry.Kind} {entry.Id} returned by drop pod: {things.Count} things");
            SaveSync.TriggerSave("transfer-restore");
        }
    }

    private static List<Thing> Contents(IMessage request)
    {
        var things = new List<Thing>();
        switch (request)
        {
            case TransferRequest send:
                foreach (var i in send.Items.Items) things.Add(ThingBlobs.DeserializeItem(i));
                foreach (var p in send.Pawns.Pawns) things.Add(ThingBlobs.DeserializePawn(p));
                break;
            case PostRequest post:
                if (post.Items is not null) foreach (var i in post.Items.Items) things.Add(ThingBlobs.DeserializeItem(i));
                if (post.Pawn is not null) things.Add(ThingBlobs.DeserializePawn(post.Pawn));
                break;
            case DepositRequest deposit:
                things.AddRange(Incoming.SilverStacks(deposit.Silver));
                break;
            case LaunchRequest launch:
                things.AddRange(Raids.RaidOutgoing.Contents(launch));
                break;
        }
        return things;
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

    private static void Run(List<Taken> taken, string successMessage, Func<ItemList, PawnList, long, IMessage> build, Func<IMessage, Task> call)
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
            Messages.Message("Rimlink.Economy.TooBig".Translate(Limits.EconomyMaxParcelBytes / 1024), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        // The save that no longer holds these things must reach the server before they can exist anywhere else.
        if (!SaveSync.TriggerSave("transfer"))
        {
            Restore(taken);
            Messages.Message("Rimlink.Economy.SaveBusy".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        bool uploaded = false;
        LongEventHandler.QueueLongEvent(() => uploaded = SaveSync.WaitForUpload(TimeSpan.FromSeconds(60)), "Rimlink.Flow.Uploading", doAsynchronously: true, null, showExtraUIInfo: false, callback: () =>
        {
            if (!uploaded || SaveSync.LastError is not null)
            {
                Restore(taken);
                Messages.Message("Rimlink.Economy.UploadFailed".Translate(SaveSync.LastError ?? "timeout"), MessageTypeDefOf.RejectInput, historical: false);
                SaveSync.TriggerSave("transfer-restore");
                return;
            }
            long tick = Find.TickManager.TicksGame;
            var request = build(items, pawnList, tick);
            var outbox = RimlinkSession.Outbox;
            var entry = outbox?.Add(request);
            _ = Task.Run(async () =>
            {
                try
                {
                    await call(request);
                    if (entry is not null) outbox!.Remove(entry.Id);
                    MainThread.Post(() => Messages.Message(successMessage, MessageTypeDefOf.PositiveEvent, historical: false));
                }
                catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
                {
                    string reason = EconomyClient.MessageOf(ex);
                    RimlinkLog.Instance.Warn($"transfer refused: {reason}");
                    if (entry is not null) outbox!.Remove(entry.Id);
                    MainThread.Post(() =>
                    {
                        Restore(taken);
                        Messages.Message("Rimlink.Economy.Failed".Translate(reason), MessageTypeDefOf.RejectInput, historical: false);
                        SaveSync.TriggerSave("transfer-restore");
                    });
                }
                catch (Exception ex)
                {
                    // Unknown outcome: the entry stays in the outbox and is replayed with the same key; nothing is restored and nothing is lost.
                    string reason = ex is RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                    RimlinkLog.Instance.Warn($"transfer unanswered, kept in the outbox: {reason}");
                    MainThread.Post(() => Messages.Message("Rimlink.Economy.Unanswered".Translate(reason), MessageTypeDefOf.NegativeEvent, historical: false));
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
