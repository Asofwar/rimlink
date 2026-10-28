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
using Transfer = Rimlink.Server.Storage.Entities.Transfer;

namespace Rimlink.Server.Application.Raids;

/// <summary>
/// Raid state machine (spec §2). Pawns of a Real raid live here in escrow until the target's game reports what
/// became of them; the server only validates and moves parcels.
/// </summary>
public sealed class RaidService(RimlinkDbContext db, ParcelValidator validator, RaidGate gate, RaidRules rules, EventBus events,
    IOptions<RaidOptions> options, ILogger<RaidService> log)
{
    private static readonly RaidStateDb[] PendingStates = [RaidStateDb.Queued, RaidStateDb.Announced];

    public async Task<Raid> Launch(long attackerId, LaunchRequest r)
    {
        gate.RequireOpen();
        var o = options.Value;
        if (r.TargetPlayerId == attackerId) throw RpcErrors.Failed(ErrorCode.SelfTrade, "you cannot raid yourself");
        if (r.Pawns is null || r.Pawns.Pawns.Count == 0) throw RpcErrors.Failed(ErrorCode.Unspecified, "a raid needs at least one pawn");
        if (r.Pawns.Pawns.Count > o.MaxRaiders) throw RpcErrors.Failed(ErrorCode.ParcelTooBig, $"too many raiders ({r.Pawns.Pawns.Count} > {o.MaxRaiders})");
        if (validator.Validate(null, r.Pawns) is { } reason) throw RpcErrors.Failed(ParcelValidator.CodeFor(reason), reason);

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if (!string.IsNullOrEmpty(r.ClientRequestId))
        {
            var existing = await db.Raids.AsNoTracking().FirstOrDefaultAsync(x => x.AttackerPlayerId == attackerId && x.ClientRequestId == r.ClientRequestId);
            if (existing is not null) return existing;
        }
        var attacker = await db.Players.FirstAsync(p => p.Id == attackerId);
        var target = await db.Players.FirstOrDefaultAsync(p => p.Id == r.TargetPlayerId)
            ?? throw RpcErrors.Failed(ErrorCode.RecipientUnknown, "no such player");
        var now = DateTime.UtcNow;
        if (await rules.CheckLaunch(attacker, target, now) is { } refusal) throw RpcErrors.Failed(refusal.Code, refusal.Message);

        var raid = new Raid
        {
            AttackerPlayerId = attackerId,
            TargetPlayerId = target.Id,
            PawnMode = (int)r.PawnMode,
            Timing = (int)r.Timing,
            Arrival = (int)r.Arrival,
            Controller = (int)RaidController.ControllerAi,
            State = RaidStateDb.Queued,
            Pawns = r.Pawns.ToByteArray(),
            LootCap = rules.LootCap(target, r.PawnMode, r.Timing),
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

        events.SendTo(target.Id, new ServerEvent { RaidQueued = new RaidQueued { Raid = ToProto(raid, attacker, target, includeBlobs: false) } });
        log.LogInformation("Player {Attacker} launched raid {Raid} at player {Target}: {Count} pawns, {Mode}/{Timing}/{Arrival}", attackerId, raid.Id, target.Id, r.Pawns.Pawns.Count, r.PawnMode, r.Timing, r.Arrival);
        return raid;
    }

    public Task<List<Raid>> Pending(long targetId) =>
        db.Raids.AsNoTracking().Where(r => r.TargetPlayerId == targetId && PendingStates.Contains(r.State)).OrderBy(r => r.Id).ToListAsync();

    public Task<List<Raid>> Mine(long attackerId) =>
        db.Raids.AsNoTracking().Where(r => r.AttackerPlayerId == attackerId).OrderByDescending(r => r.Id).Take(50).ToListAsync();

    public async Task<Raid> Get(long playerId, long raidId) =>
        await db.Raids.AsNoTracking().FirstOrDefaultAsync(r => r.Id == raidId && (r.AttackerPlayerId == playerId || r.TargetPlayerId == playerId))
        ?? throw new RpcException(new Status(StatusCode.NotFound, "no such raid"));

    /// <summary>The target's game has the raid: Warned raids land one game day of the target's play later, Instant ones now.</summary>
    public async Task<Raid> Announce(long targetId, long raidId, long gameTick)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var raid = await TargetRaid(targetId, raidId);
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
        if (raid.State != RaidStateDb.Announced) throw new RpcException(new Status(StatusCode.FailedPrecondition, "the raid has not been announced"));
        raid.State = RaidStateDb.Landed;
        raid.LandedTick = gameTick;
        var target = await db.Players.FirstAsync(p => p.Id == targetId);
        if (gameTick > target.LastOutgoingTick) target.LastOutgoingTick = gameTick;
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        events.SendTo(raid.AttackerPlayerId, new ServerEvent { RaidLanded = new RaidLanded { RaidId = raid.Id, Target = target.ToRef() } });
        log.LogInformation("Raid {Raid} landed at player {Target} at tick {Tick}", raid.Id, targetId, gameTick);
    }

    /// <summary>A raid of the target in a state the client may still act on; gone (cancelled, expired, resolved, missing) is RAID_GONE.</summary>
    private async Task<Raid> TargetRaid(long targetId, long raidId)
    {
        var raid = await db.Raids.FirstOrDefaultAsync(r => r.Id == raidId && r.TargetPlayerId == targetId);
        if (raid is null || raid.State is RaidStateDb.Cancelled or RaidStateDb.Expired or RaidStateDb.Resolved)
            throw RpcErrors.Failed(ErrorCode.RaidGone, "that raid is over");
        return raid;
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
        var flags = new PlayerFlags { Peaceful = RaidRules.IsPeaceful(p, now), VisitsClosed = p.VisitsClosed };
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
