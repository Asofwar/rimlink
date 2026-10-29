using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using LiveSessionRow = Rimlink.Server.Storage.Entities.LiveSession;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Player = Rimlink.Server.Storage.Entities.Player;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;
using Grpc.Core;

namespace Rimlink.Server.Application.Live;

/// <summary>
/// Live session state machine (3b spec §2). A LiveRaid keeps its escrow and rules in a 3a raid row with
/// controller Live (invisible to the 3a pipeline until a fallback hands it over); a LiveVisit escrows its pawns here.
/// The host's report ends either kind through <see cref="RaidService.Report"/> or <see cref="ReportVisit"/>.
/// </summary>
public sealed class LiveSessionService(RimlinkDbContext db, RaidService raids, RaidRules rules, ParcelValidator validator, MailboxService mailbox,
    SessionRegistry registry, EventBus events, LiveRelay relay, IOptions<LiveOptions> options, IOptions<RaidOptions> raidOptions, ILogger<LiveSessionService> log)
{
    private static readonly LiveStateDb[] FinalStates = [LiveStateDb.Ended, LiveStateDb.Fallback];

    public async Task<LiveSessionRow> Propose(long guestId, LiveProposal p)
    {
        if (!options.Value.Enabled) throw RpcErrors.Failed(ErrorCode.LiveDisabled, "live sessions are disabled on this server");
        if (p.HostPlayerId == guestId) throw RpcErrors.Failed(ErrorCode.SelfTrade, "you cannot visit or raid yourself");
        if (p.Pawns is null || p.Pawns.Pawns.Count == 0) throw RpcErrors.Failed(ErrorCode.Unspecified, "a live session needs at least one pawn");
        if (p.Pawns.Pawns.Count > raidOptions.Value.MaxRaiders) throw RpcErrors.Failed(ErrorCode.ParcelTooBig, $"too many pawns ({p.Pawns.Pawns.Count} > {raidOptions.Value.MaxRaiders})");
        if (!string.IsNullOrEmpty(p.ClientRequestId))
        {
            var existing = await db.LiveSessions.AsNoTracking().FirstOrDefaultAsync(s => s.GuestPlayerId == guestId && s.ClientRequestId == p.ClientRequestId);
            if (existing is not null) return existing;
        }
        var host = await db.Players.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.HostPlayerId) ?? throw RpcErrors.Failed(ErrorCode.RecipientUnknown, "no such player");
        var guest = await db.Players.FirstAsync(x => x.Id == guestId);
        if (registry.ByPlayer(host.Id) is not { AtHome: true }) throw RpcErrors.Failed(ErrorCode.HostOffline, $"{host.Name} is not online at their colony");
        if (await db.LiveSessions.AnyAsync(s => !FinalStates.Contains(s.State) && (s.GuestPlayerId == guestId || s.HostPlayerId == guestId)))
            throw RpcErrors.Failed(ErrorCode.LiveActive, "you already have a live session");
        if (await db.LiveSessions.AnyAsync(s => !FinalStates.Contains(s.State) && (s.GuestPlayerId == host.Id || s.HostPlayerId == host.Id)))
            throw RpcErrors.Failed(ErrorCode.LiveActive, $"{host.Name} is in a live session already");
        if (validator.Validate(null, p.Pawns) is { } reason) throw RpcErrors.Failed(ParcelValidator.CodeFor(reason), reason);

        var session = new LiveSessionRow { Kind = (int)p.Kind, HostPlayerId = host.Id, GuestPlayerId = guestId, State = LiveStateDb.Proposed, CreatedAt = DateTime.UtcNow,
            ClientRequestId = string.IsNullOrEmpty(p.ClientRequestId) ? null : p.ClientRequestId };
        if (p.Kind == LiveKind.LiveRaid)
        {
            // The raid row carries the escrow, the rules and the attacker's active-raid slot; Launch stamps LastOutgoingTick.
            var raid = await raids.Launch(guestId, new LaunchRequest
            {
                ClientRequestId = string.IsNullOrEmpty(p.ClientRequestId) ? "" : "live:" + p.ClientRequestId,
                TargetPlayerId = host.Id,
                PawnMode = PawnMode.PawnReal,
                Timing = p.TimingFallback,
                Arrival = p.Arrival,
                GameTick = p.GameTick,
                Pawns = p.Pawns,
            }, RaidController.ControllerLive);
            session.RaidId = raid.Id;
        }
        else
        {
            session.Pawns = p.Pawns.ToByteArray();
            if (p.GameTick > guest.LastOutgoingTick) guest.LastOutgoingTick = p.GameTick; // the pawns left the guest's game
        }
        db.LiveSessions.Add(session);
        await db.SaveChangesAsync();
        LiveMetrics.Proposed.WithLabels(p.Kind.ToString()).Inc();
        log.LogInformation("Player {Guest} proposed a {Kind} to player {Host} (live session {Id})", guestId, p.Kind, host.Id, session.Id);

        if (host.AutoAcceptLive) await Start(session);
        else events.SendTo(host.Id, new ServerEvent { LiveInvite = new LiveInvite { Session = await ToProto(session), InviteSeconds = options.Value.InviteSeconds } });
        return session;
    }

    public async Task<LiveSessionRow> Respond(long hostId, long sessionId, bool accept)
    {
        var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.HostPlayerId == hostId);
        if (session is null || session.State != LiveStateDb.Proposed) throw RpcErrors.Failed(ErrorCode.LiveGone, "that invitation is no longer open");
        if (accept) await Start(session); else await Fallback(session);
        return session;
    }

    /// <summary>The guest before the start: the pawns come back and nothing falls back. Either side afterwards: the session ends (the host owes the report).</summary>
    public async Task Cancel(long playerId, long sessionId)
    {
        var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId && (s.HostPlayerId == playerId || s.GuestPlayerId == playerId));
        if (session is null || FinalStates.Contains(session.State) || session.State == LiveStateDb.Ending) throw RpcErrors.Failed(ErrorCode.LiveGone, "that live session is over");
        bool guest = session.GuestPlayerId == playerId;
        if (session.State == LiveStateDb.Proposed)
        {
            if (!guest) { await Fallback(session); return; }
            session.State = LiveStateDb.Ended;
            session.EndReason = (int)LiveEndReason.EndGuestCancelled;
            session.EndedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await ReturnEscrow(session, "live session cancelled");
            LiveMetrics.Ended(session);
            events.SendTo(session.HostPlayerId, new ServerEvent { LiveEnded = new LiveEnded { Session = await ToProto(session) } });
            log.LogInformation("Live session {Id} cancelled by its guest before the start", session.Id);
            return;
        }
        await MarkEnding(session, guest ? LiveEndReason.EndGuestCancelled : LiveEndReason.EndHostAborted);
    }

    private async Task Start(LiveSessionRow session)
    {
        session.State = LiveStateDb.Starting;
        session.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var proto = await ToProto(session);
        events.SendTo(session.HostPlayerId, new ServerEvent { LiveStarted = new LiveStarted { Session = proto } });
        events.SendTo(session.GuestPlayerId, new ServerEvent { LiveStarted = new LiveStarted { Session = proto } });
        log.LogInformation("Live session {Id} starting", session.Id);
    }

    /// <summary>Declined or unanswered: a raid goes on as a 3a raid with the same pawns; a visit's pawns come home and the guest may visit a copy instead.</summary>
    public async Task Fallback(LiveSessionRow session)
    {
        session.State = LiveStateDb.Fallback;
        session.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (session.RaidId is { } raidId) await raids.HandOverToAi(raidId);
        else await ReturnEscrow(session, "live visit declined");
        LiveMetrics.Ended(session);
        events.SendTo(session.GuestPlayerId, new ServerEvent { LiveFallback = new LiveFallback { Session = await ToProto(session) } });
        log.LogInformation("Live session {Id} fell back to 3a", session.Id);
    }

    private async Task ReturnEscrow(LiveSessionRow session, string reason)
    {
        if (session.RaidId is { } raidId)
        {
            await raids.Cancel(raidId, reason, returnPawns: true);
            return;
        }
        if (session.Pawns is not { Length: > 0 }) return;
        var parcel = await mailbox.Create(session.GuestPlayerId, null, ParcelKindDb.RaidReturn, null, PawnList.Parser.ParseFrom(session.Pawns), 0, null, ignoreMailboxLimit: true);
        session.Pawns = null;
        await db.SaveChangesAsync();
        await mailbox.Announce(parcel);
    }

    /// <summary>The session a stream may attach to, or LIVE_GONE: the player must be its host or guest and it must be open.</summary>
    public async Task<LiveSessionRow> ForStream(long sessionId, long playerId, int role)
    {
        var session = await db.LiveSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null || session.State is not (LiveStateDb.Starting or LiveStateDb.Running or LiveStateDb.Waiting)) throw RpcErrors.Failed(ErrorCode.LiveGone, "that live session is not open");
        bool host = session.HostPlayerId == playerId, guest = session.GuestPlayerId == playerId;
        if (!(host && role == 1) && !(guest && role == 2)) throw new RpcException(new Status(StatusCode.PermissionDenied, "not your role in this live session"));
        return session;
    }

    public async Task SetSnapshot(long sessionId, int version)
    {
        var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null || FinalStates.Contains(session.State)) return;
        session.SnapshotVersion = version;
        await db.SaveChangesAsync();
    }

    public async Task MarkRunning(long sessionId)
    {
        var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null || session.State is not (LiveStateDb.Starting or LiveStateDb.Waiting)) return;
        bool back = session.State == LiveStateDb.Waiting;
        session.State = LiveStateDb.Running;
        session.WaitingSince = null;
        await db.SaveChangesAsync();
        if (back)
        {
            foreach (long id in new[] { session.HostPlayerId, session.GuestPlayerId })
                events.SendTo(id, new ServerEvent { LivePeerBack = new LivePeerBack { SessionId = session.Id } });
        }
        log.LogInformation("Live session {Id} running", session.Id);
    }

    public async Task MarkWaiting(long sessionId, long lostPlayerId)
    {
        var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null || session.State is not (LiveStateDb.Running or LiveStateDb.Starting)) return;
        session.State = LiveStateDb.Waiting;
        session.WaitingSince = DateTime.UtcNow;
        session.LostPlayerId = lostPlayerId;
        await db.SaveChangesAsync();
        long other = lostPlayerId == session.HostPlayerId ? session.GuestPlayerId : session.HostPlayerId;
        events.SendTo(other, new ServerEvent { LivePeerLost = new LivePeerLost { SessionId = session.Id } });
        log.LogInformation("Live session {Id} waiting: player {Lost} is gone", session.Id, lostPlayerId);
    }

    public async Task MarkEnding(long sessionId, LiveEndReason reason)
    {
        var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null || FinalStates.Contains(session.State) || session.State == LiveStateDb.Ending) return;
        await MarkEnding(session, reason);
    }

    private async Task MarkEnding(LiveSessionRow session, LiveEndReason reason)
    {
        session.State = LiveStateDb.Ending;
        session.EndReason = (int)reason;
        session.WaitingSince = null;
        await db.SaveChangesAsync();
        relay.CloseSession(session.Id, reason); // streams still open (an admin end, a timeout) learn it from their own channel
        var proto = await ToProto(session);
        events.SendTo(session.HostPlayerId, new ServerEvent { LiveEnded = new LiveEnded { Session = proto } });
        events.SendTo(session.GuestPlayerId, new ServerEvent { LiveEnded = new LiveEnded { Session = proto } });
        log.LogInformation("Live session {Id} ending: {Reason}", session.Id, reason);
    }

    /// <summary>The host's report for a live raid went through <see cref="RaidService.Report"/>: the session is over.</summary>
    public async Task Ended(long raidId)
    {
        var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.RaidId == raidId);
        if (session is null || FinalStates.Contains(session.State)) return;
        await Close(session);
    }

    /// <summary>The host's report for a live visit: every living pawn comes home as one parcel; loot, captives and dead pawns are not the guest's.</summary>
    public async Task ReportVisit(long hostId, RaidReport r)
    {
        var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == r.LiveSessionId && s.HostPlayerId == hostId);
        if (session is null || session.RaidId is not null) throw RpcErrors.Failed(ErrorCode.LiveGone, "no such live visit");
        if (FinalStates.Contains(session.State)) return;
        int count = PawnList.Parser.ParseFrom(session.Pawns ?? []).Pawns.Count;
        var survivors = new PawnList();
        var seen = new HashSet<int>();
        foreach (var o in r.Outcomes)
        {
            if (o.Index < 0 || o.Index >= count || !seen.Add(o.Index)) throw new RpcException(new Status(StatusCode.InvalidArgument, $"outcome index {o.Index} is out of range or repeated"));
            if (o.Outcome is PawnOutcome.OutcomeAlive or PawnOutcome.OutcomeFled)
            {
                if (o.Blob is null || o.Blob.ScribeXmlZstd.Length == 0) throw new RpcException(new Status(StatusCode.InvalidArgument, $"living visitor {o.Index} has no blob"));
                survivors.Pawns.Add(o.Blob);
            }
        }
        if (survivors.Pawns.Count > 0 && validator.Validate(null, survivors) is { } reason) throw RpcErrors.Failed(ParcelValidator.CodeFor(reason), reason);

        Parcel? parcel = null;
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            await db.Entry(session).ReloadAsync();
            if (FinalStates.Contains(session.State)) return;
            if (survivors.Pawns.Count > 0)
                parcel = await mailbox.Create(session.GuestPlayerId, hostId, ParcelKindDb.RaidReturn, null, survivors, 0, null, ignoreMailboxLimit: true);
            session.Pawns = null;
            var host = await db.Players.FirstAsync(x => x.Id == hostId);
            if (r.GameTick > host.LastOutgoingTick) host.LastOutgoingTick = r.GameTick; // the visitors left the host's save
            db.Transfers.Add(new Transfer { FromPlayerId = hostId, ToPlayerId = session.GuestPlayerId, Kind = "live-visit", GameTick = r.GameTick, Summary = $"live visit {session.Id}: {survivors.Pawns.Count} pawns home", At = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        if (parcel is not null) await mailbox.Announce(parcel);
        await Close(session);
    }

    private async Task Close(LiveSessionRow session)
    {
        bool announced = session.State == LiveStateDb.Ending;
        session.State = LiveStateDb.Ended;
        session.EndReason ??= (int)LiveEndReason.EndNormal;
        session.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        LiveMetrics.Ended(session);
        var proto = await ToProto(session);
        events.SendTo(session.GuestPlayerId, new ServerEvent { LiveEnded = new LiveEnded { Session = proto } });
        if (!announced) events.SendTo(session.HostPlayerId, new ServerEvent { LiveEnded = new LiveEnded { Session = proto } });
        log.LogInformation("Live session {Id} ended", session.Id);
    }

    /// <summary>The player's open session, if any (the attacker-side view of a fallen-back raid lives in the raid list).</summary>
    public async Task<LiveSessionRow> Current(long playerId) =>
        await db.LiveSessions.AsNoTracking().Where(s => !FinalStates.Contains(s.State) && (s.HostPlayerId == playerId || s.GuestPlayerId == playerId)).OrderByDescending(s => s.Id).FirstOrDefaultAsync()
        ?? throw RpcErrors.Failed(ErrorCode.LiveGone, "no live session");

    /// <summary>Sessions whose end the host's game still has to play out (serialise the guest's pawns, report).</summary>
    public Task<List<LiveSessionRow>> PendingEnds(long hostId) =>
        db.LiveSessions.AsNoTracking().Where(s => s.HostPlayerId == hostId && s.State == LiveStateDb.Ending).OrderBy(s => s.Id).ToListAsync();

    /// <summary>Invitations past InviteSeconds fall back; Waiting past ReconnectWaitMinutes ends with the lost side's reason.</summary>
    public async Task<int> ExpireDue(DateTime now)
    {
        int n = 0;
        var inviteBefore = now - TimeSpan.FromSeconds(options.Value.InviteSeconds);
        foreach (var session in await db.LiveSessions.Where(s => s.State == LiveStateDb.Proposed && s.CreatedAt <= inviteBefore).ToListAsync())
        {
            await Fallback(session);
            n++;
        }
        var waitBefore = now - TimeSpan.FromMinutes(options.Value.ReconnectWaitMinutes);
        foreach (var session in await db.LiveSessions.Where(s => s.State == LiveStateDb.Waiting && s.WaitingSince <= waitBefore).ToListAsync())
        {
            await MarkEnding(session, session.LostPlayerId == session.HostPlayerId ? LiveEndReason.EndHostLost : LiveEndReason.EndGuestLost);
            n++;
        }
        LiveMetrics.Active.Set(await db.LiveSessions.CountAsync(s => !FinalStates.Contains(s.State)));
        return n;
    }

    public async Task<PlayerFlags> SetAutoAccept(long playerId, bool enabled)
    {
        var player = await db.Players.FirstAsync(x => x.Id == playerId);
        player.AutoAcceptLive = enabled;
        await db.SaveChangesAsync();
        var flags = RaidService.FlagsOf(player);
        events.SendTo(playerId, new ServerEvent { PlayerFlagsChanged = new PlayerFlagsChanged { Flags = flags } });
        return flags;
    }

    public Task<List<LiveSessionRow>> List(long? playerId)
    {
        var query = db.LiveSessions.AsNoTracking();
        if (playerId is { } id) query = query.Where(s => s.HostPlayerId == id || s.GuestPlayerId == id);
        return query.OrderByDescending(s => s.Id).Take(200).ToListAsync();
    }

    public async Task<Protocol.V1.LiveSession> ToProto(LiveSessionRow s)
    {
        var players = await db.Players.AsNoTracking().Where(p => p.Id == s.HostPlayerId || p.Id == s.GuestPlayerId).ToListAsync();
        return ToProto(s, players.FirstOrDefault(p => p.Id == s.HostPlayerId), players.FirstOrDefault(p => p.Id == s.GuestPlayerId));
    }

    public static Protocol.V1.LiveSession ToProto(LiveSessionRow s, Player? host, Player? guest) => new()
    {
        Id = s.Id,
        Kind = (LiveKind)s.Kind,
        Host = host?.ToRef(),
        Guest = guest?.ToRef(),
        State = (LiveState)s.State,
        SnapshotVersion = s.SnapshotVersion ?? 0,
        RaidId = s.RaidId ?? 0,
        EndReason = (LiveEndReason)(s.EndReason ?? 0),
        CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc)),
    };
}
