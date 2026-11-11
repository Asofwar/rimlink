using Google.Protobuf;
using Grpc.Core;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Economy;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Raids;

/// <summary>
/// Launching a raid at another player. Real raids run the transfer pipeline (pawns leave the game, save, upload,
/// RPC, outbox); Snapshot raids send copies and nothing leaves. Main thread entry points.
/// </summary>
public static class RaidOutgoing
{
    public static string? CheckLimits(int pawns)
    {
        if (GuildCapture.BattleGameContext.Active) return "Rimlink.Guild.BattleIsolation".Translate();
        if (pawns == 0) return "Rimlink.Raid.NoPawns".Translate();
        if (pawns > Limits.EconomyMaxPawnsPerParcel) return "Rimlink.Economy.TooManyPawns".Translate(Limits.EconomyMaxPawnsPerParcel);
        return null;
    }

    /// <summary>From a caravan on the target's tile. A colonist must stay to lead what remains of the caravan (Real mode).</summary>
    public static void LaunchFromCaravan(Caravan caravan, PlayerRef target, IReadOnlyList<Pawn> pawns, PawnMode mode, RaidTiming timing)
    {
        var raids = RimlinkSession.Raids!;
        if (CheckLimits(pawns.Count) is { } limit) { Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false); return; }
        if (mode == PawnMode.PawnSnapshot)
        {
            LaunchSnapshot(target, pawns, timing, RaidArrival.ArrivalEdge);
            return;
        }
        if (!caravan.PawnsListForReading.Except(pawns).Any(caravan.IsOwner))
        {
            Messages.Message("Rimlink.Economy.KeepOneColonist".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Outgoing.RunWithCaravanPawns(caravan, pawns, "Rimlink.Raid.Launched".Translate(target.Name),
            (pawnList, tick) => Build(target, PawnMode.PawnReal, timing, RaidArrival.ArrivalEdge, pawnList, tick),
            request => raids.Launch((LaunchRequest)request, CancellationToken.None));
    }

    /// <summary>Dev harness: raid straight from the home map (no caravan). Real pawns leave the map and come back where they stood on a refusal.</summary>
    public static void LaunchFromCaravanless(Map map, PlayerRef target, IReadOnlyList<Pawn> pawns, PawnMode mode, RaidTiming timing)
    {
        var raids = RimlinkSession.Raids!;
        if (CheckLimits(pawns.Count) is { } limit) { Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false); return; }
        if (mode == PawnMode.PawnSnapshot) { LaunchSnapshot(target, pawns, timing, RaidArrival.ArrivalEdge); return; }
        Outgoing.RunWithMapPawns(map, pawns, "Rimlink.Raid.Launched".Translate(target.Name),
            (pawnList, tick) => Build(target, PawnMode.PawnReal, timing, RaidArrival.ArrivalEdge, pawnList, tick),
            request => raids.Launch((LaunchRequest)request, CancellationToken.None));
    }

    /// <summary>From transport pods that landed on the target's tile: everyone in the holding caravan goes, by pod, Real mode only.</summary>
    public static void LaunchFromPods(Caravan caravan, PlayerRef target, RaidTiming timing)
    {
        var raids = RimlinkSession.Raids!;
        var pawns = caravan.PawnsListForReading.ToList();
        if (CheckLimits(pawns.Count) is { } limit)
        {
            Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false);
            ReturnPodPawnsHome(caravan);
            return;
        }
        Outgoing.RunWithPodPawns(Disband(caravan), "Rimlink.Raid.Launched".Translate(target.Name),
            (pawnList, tick) => Build(target, PawnMode.PawnReal, timing, RaidArrival.ArrivalPods, pawnList, tick),
            request => raids.Launch((LaunchRequest)request, CancellationToken.None));
    }

    /// <summary>The holding caravan of a pod raid that will not happen after all: everyone flies back home.</summary>
    public static void ReturnPodPawnsHome(Caravan caravan) => ReturnPodPawnsHome(Disband(caravan));

    /// <summary>A whole caravan goes to a live session: its pawns are taken out for the transfer pipeline.</summary>
    public static List<Pawn> DisbandForLive(Caravan caravan) => Disband(caravan);

    /// <summary>Takes every pawn out of the caravan (unspawned, with their inventory) and removes the empty caravan from the world.</summary>
    private static List<Pawn> Disband(Caravan caravan)
    {
        if (GuildCapture.BattleGameContext.Active) throw new InvalidOperationException("battle cannot disband a personal caravan");
        var pawns = caravan.PawnsListForReading.ToList();
        foreach (var pawn in pawns) caravan.RemovePawn(pawn);
        if (!caravan.Destroyed) caravan.Destroy();
        return pawns;
    }

    /// <summary>Pawns taken out of pods that will not raid after all come back home by pod.</summary>
    public static void ReturnPodPawnsHome(IEnumerable<Pawn> pawns)
    {
        if (GuildCapture.BattleGameContext.Active) return;
        var home = Find.AnyPlayerHomeMap;
        if (home is null) return;
        var list = pawns.Cast<Thing>().ToList();
        if (list.Count == 0) return;
        DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(home), home, list, openDelay: 110, canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: false, forbid: false);
        Messages.Message("Rimlink.Raid.PodsReturned".Translate(), new LookTargets(DropCellFinder.TradeDropSpot(home), home), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    /// <summary>Copies leave the game. Tagged pawns need an accepted capture of their current state.</summary>
    private static void LaunchSnapshot(PlayerRef target, IReadOnlyList<Pawn> pawns, RaidTiming timing, RaidArrival arrival)
    {
        if (!RimlinkSession.Active || RimlinkSession.Raids is not { } raids) return;
        var list = new PawnList(); var captures = new List<Rimlink.Protocol.V1.OutpostWorkerCapture>();
        try
        {
            foreach (var pawn in pawns)
            {
                if (Outposts.PawnCustodyRegistry.CurrentRegistry?.Capture(pawn) is null)
                {
                    list.Pawns.Add(ThingBlobs.SerializePawn(pawn));
                    continue;
                }
                var worker = Outposts.OutpostWorkerCapture.Capture(pawn, profile: null).Worker
                    ?? throw new InvalidDataException("snapshot pawn has no current custody capture");
                var mirror = worker.Pawn.Clone(); mirror.Registry.NonAuthoritativeMirror = true;
                list.Pawns.Add(mirror); captures.Add(worker);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not serialise raiders: " + ex);
            Messages.Message("Rimlink.Raid.Refused".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        if (ThingBlobs.SizeOf(null, list) > Limits.EconomyMaxParcelBytes)
        {
            Messages.Message("Rimlink.Economy.TooBig".Translate(Limits.EconomyMaxParcelBytes / 1024), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        var request = Build(target, PawnMode.PawnSnapshot, timing, arrival, list, Find.TickManager.TicksGame);
        var outbox = RimlinkSession.Outbox;
        var connection = RimlinkSession.Connection!;
        var authentication = connection.Login!;
        var game = Current.Game;
        Task<SaveSync.UploadResult>? accepted = null;
        if (captures.Count > 0)
        {
            try
            {
                var metadata = game.GetComponent<RimlinkGameComponent>().OutpostRecovery.Metadata;
                foreach (var capture in captures)
                {
                    var previous = metadata.WorkerCaptures.SingleOrDefault(c => c.PawnCustodyId == capture.PawnCustodyId);
                    if (previous is not null) metadata.WorkerCaptures.Remove(previous);
                    metadata.WorkerCaptures.Add(capture);
                }
                accepted = SaveSync.SaveExactNow();
            }
            catch (Exception ex)
            {
                Messages.Message("Rimlink.Raid.Refused".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
        }
        var entry = accepted is null ? outbox?.Add(request) : null;
        _ = Task.Run(async () =>
        {
            try
            {
                if (accepted is not null)
                {
                    var saved = await accepted;
                    if (!saved.Uploaded) throw saved.Error ?? new IOException("snapshot custody capture was not accepted");
                    entry = await MainThread.Get(() =>
                    {
                        if (RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, authentication) || Current.Game != game)
                            throw new OperationCanceledException("snapshot raid owner changed");
                        return outbox?.Add(request);
                    });
                }
                var raid = await connection.ForAuthentication(authentication, () => raids.Launch(request, CancellationToken.None));
                if (entry is not null) outbox!.Remove(entry.Id);
                MainThread.Post(() =>
                {
                    if (RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, authentication) || Current.Game != game) return;
                    RimlinkSession.State.Raids.Mine.Insert(0, raid);
                    Messages.Message("Rimlink.Raid.Launched".Translate(target.Name), MessageTypeDefOf.PositiveEvent, historical: false);
                });
            }
            catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
            {
                var refusal = Rimlink.Client.Net.RpcErrorDetail.Read(ex);
                if (entry is not null) outbox!.Remove(entry.Id);
                MainThread.Post(() => Messages.Message("Rimlink.Raid.Refused".Translate(RefusalText.Of(refusal)), MessageTypeDefOf.RejectInput, historical: false));
            }
            catch (Exception ex)
            {
                var refusal = Rimlink.Client.Net.RpcErrorDetail.Read(ex);
                RimlinkLog.Instance.Warn(entry is null ? $"raid launch stopped: {refusal.Message}"
                    : $"raid launch unanswered, kept in the outbox: {refusal.Message}");
                MainThread.Post(() => Messages.Message("Rimlink.Economy.Unanswered".Translate(RefusalText.Of(refusal)), MessageTypeDefOf.NegativeEvent, historical: false));
            }
        });
    }

    private static LaunchRequest Build(PlayerRef target, PawnMode mode, RaidTiming timing, RaidArrival arrival, PawnList pawns, long tick) => new()
    {
        ClientRequestId = EconomyClient.NewRequestId(),
        TargetPlayerId = target.PlayerId,
        PawnMode = mode,
        Timing = timing,
        Arrival = arrival,
        Pawns = pawns,
        GameTick = tick,
    };

    /// <summary>A refused Launch entry found at login: its Real pawns come back by drop pod (the same path as refused transfers).</summary>
    public static List<Thing> Contents(LaunchRequest request)
    {
        var things = new List<Thing>();
        if (request.PawnMode == PawnMode.PawnReal) foreach (var p in request.Pawns.Pawns) things.Add(ThingBlobs.DeserializePawn(p));
        return things;
    }
}
