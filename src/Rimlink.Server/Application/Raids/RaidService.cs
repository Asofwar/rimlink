using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Raid = Rimlink.Server.Storage.Entities.Raid;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;
using Rimlink.Server.Application.Feed;

namespace Rimlink.Server.Application.Raids;

/// <summary>
/// Raid state machine (spec §2). Pawns of a Real raid live here in escrow until the target's game reports what
/// became of them; the server only validates and moves parcels.
/// </summary>
public sealed class RaidService(RimlinkDbContext db, ParcelValidator validator, RaidReportValidator reports, MailboxService mailbox, RaidGate gate, RaidRules rules,
    Economy.PlayerFreeze freezes, EventBus events, AdminFeed feed, IOptions<RaidOptions> options, ILogger<RaidService> log, ServerWealth wealth)
{
    private static readonly RaidStateDb[] FinalStates = [RaidStateDb.Resolved, RaidStateDb.Expired, RaidStateDb.Cancelled];
    private static readonly RaidStateDb[] PendingStates = [RaidStateDb.Queued, RaidStateDb.Announced];

    /// <param name="controller">Live raids (3b) stay invisible to the target's 3a pipeline until <see cref="HandOverToAi"/>.</param>
    public async Task<Raid> Launch(long attackerId, LaunchRequest r, RaidController controller = RaidController.ControllerAi)
    {
        gate.RequireOpen();
        var o = options.Value;
        if (r.TargetPlayerId == attackerId) throw RpcErrors.Failed(ErrorCode.SelfTrade, RefusalKeys.RaidsSelf, "you cannot raid yourself");
        if (r.Pawns is null || r.Pawns.Pawns.Count == 0) throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.RaidsNoPawns, "a raid needs at least one pawn");
        if (r.Pawns.Pawns.Count > o.MaxRaiders) throw RpcErrors.Failed(ErrorCode.ParcelTooBig, RefusalKeys.RaidsTooManyPawns, $"too many raiders ({r.Pawns.Pawns.Count} > {o.MaxRaiders})", r.Pawns.Pawns.Count, o.MaxRaiders);
        if (validator.Validate(null, r.Pawns) is { } reason) throw RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args);

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if (!string.IsNullOrEmpty(r.ClientRequestId))
        {
            var existing = await db.Raids.AsNoTracking().FirstOrDefaultAsync(x => x.AttackerPlayerId == attackerId && x.ClientRequestId == r.ClientRequestId);
            if (existing is not null) return existing;
        }
        await freezes.Require(attackerId); // raiders carry loot home: a frozen player starts none (after the replay lookup, as in EconomyGate)
        var attacker = await db.Players.FirstAsync(p => p.Id == attackerId);
        var target = await db.Players.FirstOrDefaultAsync(p => p.Id == r.TargetPlayerId)
            ?? throw RpcErrors.Failed(ErrorCode.RecipientUnknown, RefusalKeys.NoSuchPlayer, "no such player");
        var now = DateTime.UtcNow;
        if (await rules.CheckLaunch(attacker, target, now) is { } refusal) throw RpcErrors.Failed(refusal.Code, refusal.Key, refusal.Message, refusal.Args);

        var raid = new Raid
        {
            AttackerPlayerId = attackerId,
            TargetPlayerId = target.Id,
            PawnMode = (int)r.PawnMode,
            Timing = (int)r.Timing,
            Arrival = (int)r.Arrival,
            Controller = (int)controller,
            State = RaidStateDb.Queued,
            Pawns = r.Pawns.ToByteArray(),
            LootCap = rules.LootCap(target, await wealth.Of(target.Id), r.PawnMode, r.Timing),
            CreatedAt = now,
            ExpiresAt = now + TimeSpan.FromDays(o.ExpireRealDays),
            ClientRequestId = string.IsNullOrEmpty(r.ClientRequestId) ? null : r.ClientRequestId,
        };
        db.Raids.Add(raid);
        // Real pawns left the attacker's game: a save older than this one would bring them back.
        if (r.PawnMode == PawnMode.PawnReal && r.GameTick > attacker.LastOutgoingTick) attacker.LastOutgoingTick = r.GameTick;
        db.Transfers.Add(new Transfer { FromPlayerId = attackerId, ToPlayerId = target.Id, Kind = "raid", GameTick = r.GameTick, Summary = TransferService.Summary(null, r.Pawns, 0), At = now });
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        RaidMetrics.Outcome("launched");
        if (controller == RaidController.ControllerAi) events.SendTo(target.Id, new ServerEvent { RaidQueued = new RaidQueued { Raid = ToProto(raid, attacker, target, includeBlobs: false) } });
        log.LogInformation("Player {Attacker} launched raid {Raid} at player {Target}: {Count} pawns, {Mode}/{Timing}/{Arrival}", attackerId, raid.Id, target.Id, r.Pawns.Pawns.Count, r.PawnMode, r.Timing, r.Arrival);
        return raid;
    }

    public Task<List<Raid>> Pending(long targetId) =>
        db.Raids.AsNoTracking().Where(r => r.TargetPlayerId == targetId && PendingStates.Contains(r.State) && r.Controller != (int)RaidController.ControllerLive).OrderBy(r => r.Id).ToListAsync();

    /// <summary>A live session fell back: the raid row becomes an ordinary 3a raid and the target's game learns about it now.</summary>
    public async Task HandOverToAi(long raidId)
    {
        var raid = await db.Raids.FirstOrDefaultAsync(r => r.Id == raidId);
        if (raid is null || raid.Controller != (int)RaidController.ControllerLive || raid.State != RaidStateDb.Queued) return;
        raid.Controller = (int)RaidController.ControllerAi;
        await db.SaveChangesAsync();
        events.SendTo(raid.TargetPlayerId, new ServerEvent { RaidQueued = new RaidQueued { Raid = await ToProto(raid, includeBlobs: false) } });
        log.LogInformation("Raid {Raid} handed over to the AI pipeline", raid.Id);
    }

    public Task<List<Raid>> Mine(long attackerId) =>
        db.Raids.AsNoTracking().Where(r => r.AttackerPlayerId == attackerId).OrderByDescending(r => r.Id).Take(50).ToListAsync();

    /// <summary>The attacker may look at any of their raids; the target only gets one it can still act on (an over raid must not land).</summary>
    public async Task<Raid> Get(long playerId, long raidId)
    {
        var raid = await db.Raids.AsNoTracking().FirstOrDefaultAsync(r => r.Id == raidId && (r.AttackerPlayerId == playerId || r.TargetPlayerId == playerId))
            ?? throw RpcErrors.With(StatusCode.NotFound, ErrorCode.Unspecified, RefusalKeys.RaidsNoSuchRaid, "no such raid");
        if (raid.AttackerPlayerId != playerId && FinalStates.Contains(raid.State)) throw RpcErrors.Failed(ErrorCode.RaidGone, RefusalKeys.RaidsGone, "that raid is over");
        return raid;
    }

    /// <summary>The target's game has the raid: Warned raids land one game day of the target's play later, Instant ones now.</summary>
    public async Task<Raid> Announce(long targetId, long raidId, long gameTick)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var raid = await TargetRaid(targetId, raidId);
        if (raid.Controller == (int)RaidController.ControllerLive) throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.Unspecified, RefusalKeys.RaidsLiveNotAnnounced, "a live raid is not announced");
        if (raid.State == RaidStateDb.Queued)
        {
            gate.RequireOpen();
            raid.State = RaidStateDb.Announced;
            raid.AnnouncedTick = gameTick;
            raid.LandAtTick = gameTick + (raid.Timing == (int)RaidTiming.TimingWarned ? options.Value.WarnTicks : 0);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            log.LogInformation("Raid {Raid} announced to player {Target} at tick {Tick}, lands at {LandAt}", raid.Id, targetId, gameTick, raid.LandAtTick);
        }
        return raid;
    }

    /// <summary>The raiders are on the target's map; a save older than this would recreate the colony from before the raid.</summary>
    public async Task Land(long targetId, long raidId, long gameTick)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var raid = await TargetRaid(targetId, raidId);
        if (raid.State == RaidStateDb.Landed) return;
        if (raid.Controller == (int)RaidController.ControllerLive) throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.Unspecified, RefusalKeys.RaidsLiveDoesNotLand, "a live raid does not land");
        if (raid.State != RaidStateDb.Announced) throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.Unspecified, RefusalKeys.RaidsNotAnnounced, "the raid has not been announced");
        raid.State = RaidStateDb.Landed;
        raid.LandedTick = gameTick;
        // The defender now has the full window to report; a landed raid nobody reports expires like a queued one.
        raid.ExpiresAt = DateTime.UtcNow + TimeSpan.FromDays(options.Value.ExpireRealDays);
        var target = await db.Players.FirstAsync(p => p.Id == targetId);
        if (gameTick > target.LastOutgoingTick) target.LastOutgoingTick = gameTick;
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        RaidMetrics.Outcome("landed");
        events.SendTo(raid.AttackerPlayerId, new ServerEvent { RaidLanded = new RaidLanded { RaidId = raid.Id, Target = target.ToRef() } });
        string attackerName = await db.Players.AsNoTracking().Where(p => p.Id == raid.AttackerPlayerId).Select(p => p.Name).FirstAsync();
        feed.Publish(FeedTypes.RaidLanded, new RaidLandedData(raid.Id, new FeedPlayer(raid.AttackerPlayerId, attackerName), new FeedPlayer(target.Id, target.Name)));
        log.LogInformation("Raid {Raid} landed at player {Target} at tick {Tick}", raid.Id, targetId, gameTick);
    }

    /// <summary>A raid of the target in a state the client may still act on; someone else's raid is NOT_FOUND, an over one is RAID_GONE.</summary>
    private async Task<Raid> TargetRaid(long targetId, long raidId, bool allowResolved = false)
    {
        var raid = await db.Raids.FirstOrDefaultAsync(r => r.Id == raidId) ?? throw RpcErrors.Failed(ErrorCode.RaidGone, RefusalKeys.RaidsGone, "that raid is over");
        if (raid.TargetPlayerId != targetId) throw RpcErrors.With(StatusCode.NotFound, ErrorCode.Unspecified, RefusalKeys.RaidsNoSuchRaid, "no such raid");
        if (raid.State is RaidStateDb.Cancelled or RaidStateDb.Expired || (raid.State == RaidStateDb.Resolved && !allowResolved))
            throw RpcErrors.Failed(ErrorCode.RaidGone, RefusalKeys.RaidsGone, "that raid is over");
        return raid;
    }

    /// <summary>The defender's game is done with the raid: survivors, loot and captives become parcels for the attacker. Returns the raid (null when it was already resolved).</summary>
    public async Task<Raid?> Report(long targetId, RaidReport r)
    {
        var parcels = new List<Parcel>();
        RaidSummary summary;
        long attackerId;
        Raid reported;
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            var raid = await TargetRaid(targetId, r.RaidId, allowResolved: true);
            if (raid.State == RaidStateDb.Resolved) return null;
            // A live raid never lands through the 3a pipeline: its guests were spawned by the host's game straight from Queued.
            bool live = raid.Controller == (int)RaidController.ControllerLive && raid.State == RaidStateDb.Queued;
            if (raid.State != RaidStateDb.Landed && !live) throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.Unspecified, RefusalKeys.RaidsNotLanded, "the raid has not landed");
            if (live)
            {
                // Guests the host's save does not know (it predates their arrival) come home as they left, from the escrow.
                r = r.Clone();
                var escrow = PawnList.Parser.ParseFrom(raid.Pawns);
                var known = r.Outcomes.Select(o => o.Index).ToHashSet();
                for (int i = 0; i < escrow.Pawns.Count; i++)
                    if (!known.Contains(i)) r.Outcomes.Add(new PawnOutcomeMsg { Index = i, Outcome = PawnOutcome.OutcomeAlive, Blob = escrow.Pawns[i] });
            }
            long lootValue = reports.Validate(raid, r);

            summary = new RaidSummary { LootValue = lootValue, Captives = r.Captives?.Pawns.Count ?? 0 };
            var survivors = new PawnList();
            foreach (var o in r.Outcomes)
            {
                db.RaidOutcomes.Add(new RaidOutcome { RaidId = raid.Id, Index = o.Index, Outcome = (int)o.Outcome });
                switch (o.Outcome)
                {
                    case PawnOutcome.OutcomeDead: summary.Dead++; break;
                    case PawnOutcome.OutcomeCaptured: summary.Captured++; break;
                    case PawnOutcome.OutcomeFled: summary.Fled++; if (raid.PawnMode == (int)PawnMode.PawnReal) survivors.Pawns.Add(o.Blob); break;
                    case PawnOutcome.OutcomeAlive: summary.Alive++; if (raid.PawnMode == (int)PawnMode.PawnReal) survivors.Pawns.Add(o.Blob); break;
                }
            }
            raid.State = RaidStateDb.Resolved;
            raid.ResolvedAt = DateTime.UtcNow;
            raid.SummaryJson = JsonFormatter.Default.Format(summary);
            attackerId = raid.AttackerPlayerId;
            reported = raid;

            if (r.Loot is { Items.Count: > 0 })
                parcels.Add(await mailbox.Create(attackerId, targetId, ParcelKindDb.Loot, r.Loot, null, 0, null, ignoreMailboxLimit: true));
            if (survivors.Pawns.Count > 0)
                parcels.Add(await mailbox.Create(attackerId, null, ParcelKindDb.RaidReturn, null, survivors, 0, null, ignoreMailboxLimit: true));
            if (r.Captives is { Pawns.Count: > 0 })
                parcels.Add(await mailbox.Create(attackerId, targetId, ParcelKindDb.Captive, null, r.Captives, 0, null, ignoreMailboxLimit: true));

            // Loot and survivors left the defender's game: a save older than this one would bring them back.
            var target = await db.Players.FirstAsync(p => p.Id == targetId);
            if (r.GameTick > target.LastOutgoingTick) target.LastOutgoingTick = r.GameTick;
            db.Transfers.Add(new Transfer { FromPlayerId = targetId, ToPlayerId = attackerId, Kind = "raid-report", GameTick = r.GameTick, Summary = $"raid {raid.Id}: {summary}", At = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            log.LogInformation("Raid {Raid} resolved by player {Target}: {Summary}", raid.Id, targetId, summary);
        }
        RaidMetrics.Outcome("resolved");
        events.SendTo(attackerId, new ServerEvent { RaidResolved = new RaidResolved { RaidId = r.RaidId, Summary = summary } });
        var names = await db.Players.AsNoTracking().Where(p => p.Id == attackerId || p.Id == targetId).ToDictionaryAsync(p => p.Id, p => p.Name);
        feed.Publish(FeedTypes.RaidResolved, new RaidResolvedData(r.RaidId, new FeedPlayer(attackerId, names.GetValueOrDefault(attackerId, "")), new FeedPlayer(targetId, names.GetValueOrDefault(targetId, "")),
            summary.Dead, summary.Captured, summary.Fled, summary.Alive, summary.LootValue, summary.Captives));
        foreach (var parcel in parcels) await mailbox.Announce(parcel);
        return reported;
    }

    /// <summary>
    /// Raids past their expiry go back to the attacker (Real pawns as a parcel): queued and announced ones the defender never
    /// played, landed ones the defender never reported (the copies on their map stay there; the originals come home).
    /// </summary>
    public async Task<int> ExpireDue(DateTime now)
    {
        var due = await db.Raids.Where(r => !FinalStates.Contains(r.State) && r.ExpiresAt <= now).ToListAsync();
        int n = 0;
        foreach (var raid in due)
        {
            try
            {
                if (await Close(raid, RaidStateDb.Expired, returnPawns: true, "expired")) n++;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not expire raid {Raid}", raid.Id);
            }
        }
        RaidMetrics.Active.Set(await db.Raids.CountAsync(r => !FinalStates.Contains(r.State)));
        return n;
    }

    /// <summary>
    /// Admin cancellation. Pawns still in escrow (not landed) go back; a landed raid's copies stay with the defender's map
    /// unless <paramref name="returnPawns"/> says the originals go home too (a raid stuck on a map nobody plays).
    /// </summary>
    public async Task<bool> Cancel(long raidId, string reason, bool returnPawns = false)
    {
        var raid = await db.Raids.FirstOrDefaultAsync(r => r.Id == raidId);
        if (raid is null || FinalStates.Contains(raid.State)) return false;
        return await Close(raid, RaidStateDb.Cancelled, returnPawns: returnPawns || raid.State != RaidStateDb.Landed, reason);
    }

    /// <summary>The player's colony is gone (reset): raids at them give the pawns back, their own raids are dropped.</summary>
    public async Task CancelAllOf(long playerId, string reason)
    {
        foreach (var raid in await db.Raids.Where(r => (r.TargetPlayerId == playerId || r.AttackerPlayerId == playerId) && !FinalStates.Contains(r.State)).ToListAsync())
            await Close(raid, RaidStateDb.Cancelled, returnPawns: raid.TargetPlayerId == playerId, reason);
    }

    /// <summary>False when the raid reached a final state between the caller's read and this transaction (a report that came in meanwhile).</summary>
    private async Task<bool> Close(Raid raid, RaidStateDb state, bool returnPawns, string reason)
    {
        Parcel? back = null;
        bool wasIncoming;
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            await db.Entry(raid).ReloadAsync();
            if (FinalStates.Contains(raid.State)) return false;
            wasIncoming = raid.State is RaidStateDb.Queued or RaidStateDb.Announced;
            raid.State = state;
            raid.ResolvedAt = DateTime.UtcNow;
            if (returnPawns && raid.PawnMode == (int)PawnMode.PawnReal)
                back = await mailbox.Create(raid.AttackerPlayerId, null, ParcelKindDb.RaidReturn, null, PawnList.Parser.ParseFrom(raid.Pawns), 0, null, ignoreMailboxLimit: true);
            db.Transfers.Add(new Transfer { FromPlayerId = raid.AttackerPlayerId, ToPlayerId = raid.TargetPlayerId, Kind = "raid-" + state.ToString().ToLowerInvariant(), GameTick = 0, Summary = $"raid {raid.Id}: {reason}", At = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        RaidMetrics.Outcome(state.ToString().ToLowerInvariant());
        if (state == RaidStateDb.Expired) events.SendTo(raid.AttackerPlayerId, new ServerEvent { RaidExpired = new RaidExpired { RaidId = raid.Id } });
        else events.SendTo(raid.AttackerPlayerId, new ServerEvent { Notice = new Notice { Text = $"Your raid was cancelled: {reason}" } });
        // The target's client still lists a raid that never landed as incoming (and keeps its economy closed): tell it.
        if (wasIncoming) events.SendTo(raid.TargetPlayerId, new ServerEvent { RaidExpired = new RaidExpired { RaidId = raid.Id } });
        if (back is not null) await mailbox.Announce(back);
        log.LogInformation("Raid {Raid} {State}: {Reason}", raid.Id, state, reason);
        return true;
    }

    public async Task<PlayerFlags> Flags(long playerId) => FlagsOf(await db.Players.AsNoTracking().FirstAsync(p => p.Id == playerId));

    public async Task<PlayerFlags> SetPeaceful(long playerId, bool on)
    {
        var player = await db.Players.FirstAsync(p => p.Id == playerId);
        var now = DateTime.UtcNow;
        if (on)
        {
            player.Peaceful = true;
            player.PeacefulOffAt = null;
        }
        else if (RaidRules.IsPeaceful(player, now))
        {
            // Leaving peaceful mode takes a day, so it cannot be switched off for one quick strike.
            player.Peaceful = false;
            player.PeacefulOffAt ??= now + TimeSpan.FromHours(options.Value.PeacefulOffDelayHours);
        }
        await db.SaveChangesAsync();
        var flags = FlagsOf(player);
        events.SendTo(playerId, new ServerEvent { PlayerFlagsChanged = new PlayerFlagsChanged { Flags = flags } });
        return flags;
    }

    public async Task<PlayerFlags> SetVisitsClosed(long playerId, bool closed)
    {
        var player = await db.Players.FirstAsync(p => p.Id == playerId);
        player.VisitsClosed = closed;
        await db.SaveChangesAsync();
        var flags = FlagsOf(player);
        events.SendTo(playerId, new ServerEvent { PlayerFlagsChanged = new PlayerFlagsChanged { Flags = flags } });
        return flags;
    }

    public static PlayerFlags FlagsOf(Player p)
    {
        var now = DateTime.UtcNow;
        var flags = new PlayerFlags { Peaceful = RaidRules.IsPeaceful(p, now), VisitsClosed = p.VisitsClosed, AutoAcceptLive = p.AutoAcceptLive, AcceptGifts = p.AcceptGifts, TradesClosed = p.TradesClosed };
        if (!p.Peaceful && p.PeacefulOffAt is { } until && until > now) flags.PeacefulOffAt = Timestamp.FromDateTime(DateTime.SpecifyKind(until, DateTimeKind.Utc));
        return flags;
    }

    public async Task<Protocol.V1.Raid> ToProto(Raid raid, bool includeBlobs)
    {
        var players = await db.Players.AsNoTracking().Where(p => p.Id == raid.AttackerPlayerId || p.Id == raid.TargetPlayerId).ToListAsync();
        return ToProto(raid, players.FirstOrDefault(p => p.Id == raid.AttackerPlayerId), players.FirstOrDefault(p => p.Id == raid.TargetPlayerId), includeBlobs);
    }

    public Protocol.V1.Raid ToProto(Raid r, Player? attacker, Player? target, bool includeBlobs)
    {
        var proto = new Protocol.V1.Raid
        {
            Id = r.Id,
            Attacker = attacker?.ToRef(),
            Target = target?.ToRef(),
            PawnMode = (PawnMode)r.PawnMode,
            Timing = (RaidTiming)r.Timing,
            Arrival = (RaidArrival)r.Arrival,
            Controller = (RaidController)r.Controller,
            State = (RaidState)r.State,
            Pawns = MailboxService.StripBlobs(PawnList.Parser.ParseFrom(r.Pawns), includeBlobs),
            LootCap = r.LootCap,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
            AnnouncedTick = r.AnnouncedTick ?? 0,
            LandAtTick = r.LandAtTick ?? 0,
            LandedTick = r.LandedTick ?? 0,
            ExpiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(r.ExpiresAt, DateTimeKind.Utc)),
            AllowKidnap = options.Value.AllowKidnap,
        };
        if (r.SummaryJson is not null) proto.Summary = RaidSummary.Parser.ParseJson(r.SummaryJson);
        return proto;
    }

    public async Task<RaidList> ToList(IEnumerable<Raid> raids)
    {
        var list = new RaidList();
        var ids = raids.SelectMany(r => new[] { r.AttackerPlayerId, r.TargetPlayerId }).Distinct().ToList();
        var players = await db.Players.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        foreach (var r in raids) list.Raids.Add(ToProto(r, players.GetValueOrDefault(r.AttackerPlayerId), players.GetValueOrDefault(r.TargetPlayerId), includeBlobs: false));
        return list;
    }
}
