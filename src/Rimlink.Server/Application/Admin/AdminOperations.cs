using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Application.Economy;
using Lot = Rimlink.Server.Storage.Entities.Lot;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Application.Live;
using LiveSessionRow = Rimlink.Server.Storage.Entities.LiveSession;
using Rimlink.Server.Application.Visits;
using ShopItem = Rimlink.Server.Storage.Entities.ShopItem;
using Raid = Rimlink.Server.Storage.Entities.Raid;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Admin;

public sealed record LotView(long Id, long SellerPlayerId, string SellerName, long Price, DateTime CreatedAt, DateTime ExpiresAt, string Status, string Summary);

public sealed record TradeView(long Id, long InitiatorId, string InitiatorName, long PartnerId, string PartnerName, string Status, int Revision,
    string InitiatorSide, string PartnerSide, DateTime CreatedAt, DateTime ExpiresAt, DateTime? ClosedAt, string? CloseReason);

public sealed record OnlinePlayer(long PlayerId, long AccountId, string Name, string FactionName, Role Role, string Ip, DateTime Since);

/// <summary>Every administrative operation, with the role check and the audit log in one place. gRPC, REST and chat commands all call this.</summary>
public sealed record ArchivedRow(long Id, long PlayerId, string? PlayerName, string Colony, int Tile, DateTime SettledAt, DateTime ArchivedAt,
    string Reason, string ArchivedBy, long WealthTotal, int? ColonyGameDays, DateTime? LastActiveAt, string State, DateTime? RestoredAt, string? RestoredBy);

public sealed class AdminOperations(
    RimlinkDbContext db,
    SessionRegistry sessions,
    EventBus events,
    BanService bans,
    ChatService chat,
    SaveStore saves,
    DataDirectory data,
    IHostApplicationLifetime lifetime,
    WalletService wallet,
    MailboxService mailbox,
    MarketService market,
    FreezeState freeze,
    RaidService raids,
    LiveSessionService live,
    RaidFreezeState raidFreeze,
    ShopService shop,
    VisitService visits,
    Trades.TradeService trades,
    Backup.BackupService backups,
    AdminFeed feed,
    PlayerFreeze playerFreeze,
    AdminAudit audit,
    World.ColonyArchive archive,
    ILogger<AdminOperations> log,
    ForbiddenDefsService forbiddenStore,
    ForbiddenDefs forbidden,
    Incidents.IncidentService incidents)
{
    /// <summary>Archived colonies, newest first (at most 500); <paramref name="state"/> is archived, restored or all.</summary>
    public async Task<List<ArchivedRow>> ListArchived(Actor actor, string state)
    {
        actor.Require(Role.Moderator);
        var query = db.ArchivedColonies.AsNoTracking();
        query = state switch
        {
            "archived" => query.Where(c => c.State == ArchiveState.Archived),
            "restored" => query.Where(c => c.State == ArchiveState.Restored),
            "all" => query,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "state must be archived, restored or all")),
        };
        return await query.OrderByDescending(c => c.ArchivedAt).ThenByDescending(c => c.Id).Take(500)
            .Select(c => new ArchivedRow(c.Id, c.PlayerId, c.Player.Name, c.Name, c.Tile, c.SettledAt, c.ArchivedAt, c.Reason, c.ArchivedBy,
                c.WealthTotal, c.ColonyGameDays, c.LastActiveAt, c.State == ArchiveState.Archived ? "archived" : "restored", c.RestoredAt, c.RestoredBy))
            .ToListAsync();
    }

    /// <summary>Sends the player's colony to the archive now (the archive writes its own audit entry).</summary>
    public async Task<World.ArchiveOutcome> ArchiveColony(Actor actor, long playerId)
    {
        actor.Require(Role.Admin);
        await FindPlayer(playerId);
        return await archive.Archive(playerId, "admin", actor);
    }

    public async Task<World.RestoreOutcome> RestoreColony(Actor actor, long archiveId)
    {
        actor.Require(Role.Admin);
        var outcome = await archive.Restore(archiveId, actor.Name);
        if (outcome is World.RestoreOutcome.Restored restored)
        {
            var row = await db.ArchivedColonies.AsNoTracking().Where(c => c.Id == archiveId).Select(c => new { c.Player.AccountId, c.Name, c.Tile }).FirstAsync();
            await Audit(actor, "restore", row.AccountId, $"{row.Name} on tile {row.Tile}");
        }
        return outcome;
    }

    public async Task<Backup.BackupFile> CreateBackup(Actor actor)
    {
        actor.Require(Role.Admin);
        var file = await backups.CreateNow();
        await Audit(actor, "backup", null, file.Name);
        return file;
    }

    /// <summary>The archive to download, or null when the name is not a backup; only an existing file is audited.</summary>
    public async Task<FileInfo?> OpenBackup(Actor actor, string name)
    {
        actor.Require(Role.Admin);
        var file = backups.Open(name);
        if (file is not null) await Audit(actor, "backup-download", null, name);
        return file;
    }

    public Task<List<ShopItem>> ListShop(Actor actor, long playerId)
    {
        actor.Require(Role.Moderator);
        return db.ShopItems.AsNoTracking().Where(s => s.HostPlayerId == playerId).OrderBy(s => s.ThingId).ToListAsync();
    }

    public Task<List<PendingRemoval>> ListRemovals(Actor actor, long playerId)
    {
        actor.Require(Role.Moderator);
        return shop.Pending(playerId);
    }

    public sealed record RaidStatsResult(int Queued, int Announced, int Landed, int ResolvedToday, int Expired, int Cancelled, bool Frozen, string? FreezeReason);

    public async Task<List<TradeView>> ListTrades(Actor actor, long? playerId)
    {
        actor.Require(Role.Moderator);
        var rows = await trades.ListFor(playerId);
        var ids = rows.SelectMany(t => new[] { t.InitiatorPlayerId, t.PartnerPlayerId }).Distinct().ToList();
        var names = await db.Players.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        string Side(byte[] items, byte[] pawns, long silver) => TransferService.Summary(ItemList.Parser.ParseFrom(items), PawnList.Parser.ParseFrom(pawns), silver);
        return rows.Select(t => new TradeView(t.Id, t.InitiatorPlayerId, names.GetValueOrDefault(t.InitiatorPlayerId, "?"), t.PartnerPlayerId, names.GetValueOrDefault(t.PartnerPlayerId, "?"),
            t.Status.ToString(), t.Revision, Side(t.InitiatorItems, t.InitiatorPawns, t.InitiatorSilver), Side(t.PartnerItems, t.PartnerPawns, t.PartnerSilver),
            t.CreatedAt, t.ExpiresAt, t.ClosedAt, t.CloseReason)).ToList();
    }

    /// <summary>Ends an open trade; each side goes back to its owner.</summary>
    public async Task<bool> CancelTrade(Actor actor, long tradeId, string reason)
    {
        actor.Require(Role.Admin);
        if (!await trades.AdminCancel(tradeId, reason)) return false;
        await Audit(actor, "trade-cancel", null, $"trade {tradeId}: {reason}");
        return true;
    }

    public Task<List<Raid>> ListRaids(Actor actor, long? playerId)
    {
        actor.Require(Role.Moderator);
        var query = db.Raids.AsNoTracking();
        if (playerId is { } id) query = query.Where(r => r.TargetPlayerId == id || r.AttackerPlayerId == id);
        return query.OrderByDescending(r => r.Id).Take(200).ToListAsync();
    }

    /// <param name="returnPawns">Also give a landed raid's pawns back to the attacker (they are otherwise left to the defender's map).</param>
    public async Task<bool> CancelRaid(Actor actor, long raidId, string reason, bool returnPawns = false)
    {
        actor.Require(Role.Admin);
        if (!await raids.Cancel(raidId, reason, returnPawns)) return false;
        await Audit(actor, "raid-cancel", null, $"raid {raidId}: {reason}{(returnPawns ? " (pawns returned)" : "")}");
        return true;
    }

    public Task<List<LiveSessionRow>> ListLive(Actor actor, long? playerId)
    {
        actor.Require(Role.Moderator);
        return live.List(playerId);
    }

    /// <summary>Ends a live session from outside: both games get LiveEnded, the host still reports the guest's pawns.</summary>
    public async Task<bool> EndLive(Actor actor, long sessionId, string reason)
    {
        actor.Require(Role.Admin);
        var row = await db.LiveSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId);
        if (row is null || row.State is LiveStateDb.Ended or LiveStateDb.Fallback) return false;
        if (row.State == LiveStateDb.Proposed) await live.Respond(row.HostPlayerId, sessionId, accept: false);
        else if (row.State == LiveStateDb.Ending) await live.CloseEnding(sessionId); // the host's report is not coming: the escrow goes home
        else await live.MarkEnding(sessionId, LiveEndReason.EndAdmin);
        await Audit(actor, "live-end", null, $"live session {sessionId}: {reason}");
        return true;
    }

    public async Task SetRaidsFrozen(Actor actor, bool frozen, string? reason)
    {
        actor.Require(Role.Admin);
        raidFreeze.Frozen = frozen;
        raidFreeze.Reason = frozen ? reason : null;
        await Audit(actor, frozen ? "raids-freeze" : "raids-unfreeze", null, reason ?? "");
        feed.Publish(FeedTypes.RaidsFrozen, new FreezeData(frozen, raidFreeze.Reason));
    }

    public async Task<RaidStatsResult> RaidStats(Actor actor)
    {
        actor.Require(Role.Moderator);
        var today = DateTime.UtcNow.Date;
        return new RaidStatsResult(
            await db.Raids.CountAsync(r => r.State == RaidStateDb.Queued),
            await db.Raids.CountAsync(r => r.State == RaidStateDb.Announced),
            await db.Raids.CountAsync(r => r.State == RaidStateDb.Landed),
            await db.Raids.CountAsync(r => r.State == RaidStateDb.Resolved && r.ResolvedAt >= today),
            await db.Raids.CountAsync(r => r.State == RaidStateDb.Expired),
            await db.Raids.CountAsync(r => r.State == RaidStateDb.Cancelled),
            raidFreeze.Frozen, raidFreeze.Reason);
    }

    /// <summary>Admin switch of peaceful mode without the player-side delay.</summary>
    public async Task SetPeaceful(Actor actor, long playerId, bool peaceful)
    {
        actor.Require(Role.Admin);
        var player = await FindPlayer(playerId);
        player.Peaceful = peaceful;
        player.PeacefulOffAt = null;
        await db.SaveChangesAsync();
        events.SendTo(playerId, new ServerEvent { PlayerFlagsChanged = new PlayerFlagsChanged { Flags = RaidService.FlagsOf(player) } });
        await Audit(actor, "peaceful", player.AccountId, peaceful ? "on" : "off");
    }

    /// <summary>Freezes or opens one player's economy (integrity spec §2); the player is told, deliveries to them go on.</summary>
    public async Task SetPlayerEconomyFrozen(Actor actor, long playerId, bool frozen, string? reason)
    {
        actor.Require(Role.Admin);
        var player = await FindPlayer(playerId);
        string details = frozen ? (string.IsNullOrWhiteSpace(reason) ? "by admin" : reason.Trim()) : "";
        await playerFreeze.Set(playerId, frozen, frozen ? details : null);
        await Audit(actor, frozen ? "player-economy-freeze" : "player-economy-unfreeze", player.AccountId, details);
    }

    public sealed record EconomyStatsResult(long SilverInWallets, int OpenLots, int PendingParcels, int TradesToday, bool Frozen, string? FreezeReason,
        long StorageFeesCollected = 0);

    public async Task<long> AdjustWallet(Actor actor, long playerId, long delta, string reason)
    {
        actor.Require(Role.Admin);
        var player = await FindPlayer(playerId);
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            await wallet.Apply(playerId, delta, "admin", 0);
            await tx.CommitAsync();
        }
        wallet.FlushEvents();
        await Audit(actor, "wallet", player.AccountId, $"{delta:+#;-#;0} silver: {reason}");
        return await wallet.Balance(playerId);
    }

    public async Task<long> WalletBalance(Actor actor, long playerId)
    {
        actor.Require(Role.Moderator);
        return await wallet.Balance(playerId);
    }

    public async Task<List<Parcel>> ListParcels(Actor actor, long playerId)
    {
        actor.Require(Role.Moderator);
        return await db.Parcels.AsNoTracking().Where(p => p.ToPlayerId == playerId).OrderByDescending(p => p.Id).Take(200).ToListAsync();
    }

    public async Task<List<Lot>> ListLots(Actor actor, long? playerId)
    {
        actor.Require(Role.Moderator);
        var lots = db.Lots.AsNoTracking().Where(l => l.Status == LotStatusDb.Open);
        if (playerId is { } id) lots = lots.Where(l => l.SellerPlayerId == id);
        return await lots.OrderByDescending(l => l.Id).Take(200).ToListAsync();
    }

    /// <summary>Open lots with the seller's name and a one-line summary of what they hold.</summary>
    public async Task<List<LotView>> ListLotViews(Actor actor, long? playerId)
    {
        var lots = await ListLots(actor, playerId);
        var sellerIds = lots.Select(l => l.SellerPlayerId).Distinct().ToList();
        var names = await db.Players.AsNoTracking().Where(p => sellerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        return lots.Select(l => new LotView(
            l.Id, l.SellerPlayerId, names.GetValueOrDefault(l.SellerPlayerId, ""), l.Price, l.CreatedAt, l.ExpiresAt, l.Status.ToString(),
            LotSummary.Of(ItemList.Parser.ParseFrom(l.Items), l.Pawn is { Length: > 0 } pawn ? PawnBlob.Parser.ParseFrom(pawn) : null))).ToList();
    }

    /// <summary>An open lot goes back to its seller as if withdrawn.</summary>
    public async Task<bool> RefundLot(Actor actor, long lotId)
    {
        actor.Require(Role.Admin);
        var lot = await db.Lots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lotId && l.Status == LotStatusDb.Open);
        if (lot is null) return false;
        await market.Withdraw(lot.SellerPlayerId, lotId, bypassGate: true, chargeStorage: false);
        await Audit(actor, "refund-lot", null, $"lot {lotId} returned to player {lot.SellerPlayerId}");
        return true;
    }

    /// <summary>An undelivered gift goes back to its sender: items/pawns as a new parcel, silver to the wallet.</summary>
    public async Task<bool> RefundParcel(Actor actor, long parcelId)
    {
        actor.Require(Role.Admin);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var parcel = await db.Parcels.FirstOrDefaultAsync(p => p.Id == parcelId && p.DeliveredAt == null);
        if (parcel is null) return false;
        // Only what can be undone cleanly: a gift goes back to its sender, a withdrawal back into the wallet.
        // A purchase or lot return has a paid counterpart; reversing it is wallet work for the admin.
        Parcel? back = null;
        long target;
        bool refusedGift = false;
        switch (parcel.Kind)
        {
            case ParcelKindDb.Gift when parcel.FromPlayerId is { } sender:
                target = sender;
                // A gift still waiting for the recipient's answer is decided now: no answer or sweep may return it again.
                if (parcel.Decision == ParcelDecisionDb.Awaiting) { parcel.Decision = ParcelDecisionDb.Refused; refusedGift = true; }
                back = await mailbox.Create(sender, null, ParcelKindDb.Admin, ItemList.Parser.ParseFrom(parcel.Items), PawnList.Parser.ParseFrom(parcel.Pawns), 0, null, ignoreMailboxLimit: true);
                if (parcel.Silver > 0) await wallet.Apply(sender, parcel.Silver, "refund", parcel.Id);
                break;
            case ParcelKindDb.Withdraw:
                target = parcel.ToPlayerId;
                await wallet.Apply(parcel.ToPlayerId, parcel.Silver, "refund", parcel.Id);
                break;
            default:
                return false;
        }
        // Marked delivered at tick 0 so no save can be "older" than it: it never drops again.
        parcel.DeliveredAt = DateTime.UtcNow;
        parcel.DeliveredTick = 0;
        db.Transfers.Add(new Transfer { FromPlayerId = parcel.ToPlayerId, ToPlayerId = target, Kind = "refund", GameTick = 0, Summary = $"parcel {parcel.Id}", At = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        wallet.FlushEvents();
        if (back is not null) await mailbox.Announce(back);
        if (refusedGift && await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parcel.ToPlayerId) is { } recipient)
        {
            // The recipient's client drops the gift from its mailbox and its letter.
            string returned = Economy.TransferService.Summary(ItemList.Parser.ParseFrom(parcel.Items), PawnList.Parser.ParseFrom(parcel.Pawns), parcel.Silver);
            events.SendTo(recipient.Id, new ServerEvent { GiftRefused = new GiftRefused { ParcelId = parcel.Id, By = recipient.ToRef(), Returned = returned } });
        }
        await Audit(actor, "refund-parcel", null, $"parcel {parcelId} refunded to player {target}");
        return true;
    }

    public async Task SetEconomyFrozen(Actor actor, bool frozen, string? reason)
    {
        actor.Require(Role.Admin);
        freeze.Frozen = frozen;
        freeze.Reason = frozen ? reason : null;
        await Audit(actor, frozen ? "economy-freeze" : "economy-unfreeze", null, reason ?? "");
        feed.Publish(FeedTypes.EconomyFrozen, new FreezeData(frozen, freeze.Reason));
        if (frozen) events.Broadcast(new ServerEvent { Notice = new Notice { Text = "Trading is paused: " + (reason ?? "maintenance") } });
        else events.Broadcast(new ServerEvent { Notice = new Notice { Text = "Trading is open again" } });
    }

    public async Task<EconomyStatsResult> EconomyStats(Actor actor)
    {
        actor.Require(Role.Moderator);
        var since = DateTime.UtcNow.Date;
        return new EconomyStatsResult(
            await db.Wallets.Where(w => w.PlayerId != WalletService.BankPlayerId).SumAsync(w => w.Balance),
            await db.Lots.CountAsync(l => l.Status == LotStatusDb.Open),
            await db.Parcels.CountAsync(p => p.DeliveredAt == null),
            await db.Transfers.CountAsync(t => t.At >= since && (t.Kind == "send" || t.Kind == "buy" || t.Kind == "post")),
            freeze.Frozen,
            freeze.Reason,
            await db.WalletEntries.Where(e => e.PlayerId == WalletService.BankPlayerId && e.Kind == "storage").SumAsync(e => e.Delta));
    }

    public Task<List<OnlinePlayer>> ListOnline(Actor actor)
    {
        actor.Require(Role.Moderator);
        return Task.FromResult(sessions.All.Select(s => new OnlinePlayer(s.PlayerId, s.AccountId, s.PlayerName, "", s.Role, s.Ip, s.StartedAt)).OrderBy(p => p.Since).ToList());
    }

    public async Task<Player> FindPlayer(long playerId) =>
        await db.Players.Include(p => p.Account).FirstOrDefaultAsync(p => p.Id == playerId) ?? throw new RpcException(new Status(StatusCode.NotFound, $"no player with id {playerId}"));

    public async Task<Player> FindPlayerByAccount(long accountId) =>
        await db.Players.Include(p => p.Account).FirstOrDefaultAsync(p => p.AccountId == accountId) ?? throw new RpcException(new Status(StatusCode.NotFound, $"no player for account {accountId}"));

    public async Task<Player> FindPlayerByName(string name) =>
        await db.Players.Include(p => p.Account).FirstOrDefaultAsync(p => p.Name.ToLower() == name.ToLower()) ?? throw new RpcException(new Status(StatusCode.NotFound, $"no player named '{name}'"));

    public async Task<string> Kick(Actor actor, long playerId, string reason)
    {
        actor.Require(Role.Moderator);
        var player = await FindPlayer(playerId);
        var session = sessions.ByPlayer(playerId);
        if (session is null) return $"{player.Name} is not online";
        if (session.Role > actor.Role) throw RpcErrors.PermissionDenied("cannot kick someone with a higher role");

        // A live session needs both players online, and the kicked client logs in again at once: it ends as an admin's end
        // would, while the kicked player's event stream still carries the end.
        await live.EndAllOf(playerId, LiveEndReason.EndAdmin);
        events.SendTo(playerId, new ServerEvent { Notice = new Notice { Text = $"You were kicked: {reason}" } });
        sessions.End(session.Token);
        await Audit(actor, "kick", player.AccountId, reason);
        return $"{player.Name} was kicked ({reason})";
    }

    public async Task<Ban> Ban(Actor actor, long? playerId, long? steamId, string? ip, string reason, TimeSpan? duration)
    {
        actor.Require(Role.Admin);
        long? accountId = null;
        if (playerId is { } id)
        {
            var player = await FindPlayer(id);
            accountId = player.AccountId;
            steamId ??= player.Account.SteamId;
        }
        var ban = await bans.Ban(new BanRequest(accountId, steamId, ip, reason, duration, actor.AccountId));
        await Audit(actor, "ban", accountId, $"ban #{ban.Id}: {reason}" + (duration is { } d ? $" for {d.TotalDays:0.#} days" : " permanently"));
        return ban;
    }

    public async Task<bool> Pardon(Actor actor, long banId)
    {
        actor.Require(Role.Admin);
        bool ok = await bans.Pardon(banId);
        if (ok) await Audit(actor, "pardon", null, $"ban #{banId}");
        return ok;
    }

    public Task<List<Ban>> ListBans(Actor actor)
    {
        actor.Require(Role.Moderator);
        return bans.ListActive();
    }

    public async Task Mute(Actor actor, long playerId, TimeSpan duration)
    {
        actor.Require(Role.Moderator);
        var player = await FindPlayer(playerId);
        await chat.Mute(playerId, duration);
        await Audit(actor, "mute", player.AccountId, $"{duration.TotalMinutes:0} min");
    }

    public async Task Unmute(Actor actor, long playerId)
    {
        actor.Require(Role.Moderator);
        var player = await FindPlayer(playerId);
        await chat.Unmute(playerId);
        await Audit(actor, "unmute", player.AccountId, "");
    }

    public async Task<List<SaveVersion>> ListSaves(Actor actor, long playerId)
    {
        actor.Require(Role.Admin);
        await FindPlayer(playerId);
        return await saves.List(playerId);
    }

    public async Task<bool> RollbackSave(Actor actor, long playerId, int version)
    {
        actor.Require(Role.Admin);
        var player = await FindPlayer(playerId);
        long targetTick = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == playerId && v.Number == version).Select(v => v.GameTick).FirstOrDefaultAsync();
        bool returnsGivenAway = targetTick < player.LastOutgoingTick;
        bool ok = await saves.Rollback(playerId, version);
        if (ok) await Audit(actor, "rollback", player.AccountId, $"to v{version}" + (returnsGivenAway ? $" (returns items given away after tick {targetTick})" : ""));
        return ok;
    }

    /// <summary>Kicks the player, archives their saves and removes their world objects; the account stays.</summary>
    public async Task ResetPlayer(Actor actor, long playerId)
    {
        actor.Require(Role.Admin);
        var player = await FindPlayer(playerId);
        if (sessions.ByPlayer(playerId) is { } session)
        {
            events.SendTo(playerId, new ServerEvent { Notice = new Notice { Text = "Your colony was reset by an admin" } });
            sessions.End(session.Token);
        }

        string archive = Path.Combine(data.Archive, $"{playerId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        await saves.Archive(playerId, archive);
        // The next colony starts from nothing: its age and wealth come from its own saves.
        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.FirstSaveAt, (DateTime?)null)
            .SetProperty(p => p.WealthTotal, 0L)
            .SetProperty(p => p.WealthItems, 0L));
        await RemoveObjectsOf(playerId);
        await raids.CancelAllOf(playerId, "colony reset");
        await incidents.CancelAllOf(playerId, "colony reset");
        await shop.ClearOf(playerId);
        await visits.EndAllOf(playerId);
        await trades.CancelAllOf(playerId, "colony reset");
        await Audit(actor, "reset", player.AccountId, archive);
        log.LogInformation("Player {Name} reset by {Actor}", player.Name, actor.Name);
    }

    public async Task Broadcast(Actor actor, string text)
    {
        actor.Require(Role.Moderator);
        events.Broadcast(new ServerEvent { Notice = new Notice { Text = text } });
        feed.Publish(FeedTypes.Notice, new NoticeData(text));
        await chat.System($"[server] {text}");
        await Audit(actor, "broadcast", null, text);
    }

    public async Task SetRole(Actor actor, long playerId, Role role)
    {
        actor.Require(Role.Admin);
        var player = await FindPlayer(playerId);
        player.Account.Role = role;
        await db.SaveChangesAsync();
        await Audit(actor, "role", player.AccountId, role.ToString());
    }

    /// <summary>Players may rename themselves; admins may rename anyone.</summary>
    public async Task RenamePlayer(Actor actor, long playerId, string name, string? factionName)
    {
        if (actor.PlayerId != playerId) actor.Require(Role.Admin);
        if (!Names.IsValid(name)) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.NameInvalid, "player name is not a valid name");
        if (factionName is not null && !Names.IsValid(factionName)) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.NameInvalid, "faction name is not a valid name");

        var player = await FindPlayer(playerId);
        if (await db.Players.AnyAsync(p => p.Name == name && p.Id != playerId))
            throw new RpcException(new Status(StatusCode.AlreadyExists, $"the name '{name}' is already taken"));
        player.Name = name;
        if (factionName is not null) player.FactionName = factionName;
        await db.SaveChangesAsync();
        await Audit(actor, "rename", player.AccountId, $"{name} / {player.FactionName}");
    }

    public async Task SaveAll(Actor actor)
    {
        actor.Require(Role.Moderator);
        events.Broadcast(new ServerEvent { Notice = new Notice { Text = "Please save your game now" } });
        await Audit(actor, "saveall", null, "");
    }

    public async Task Shutdown(Actor actor, int inSeconds, string reason)
    {
        actor.Require(Role.Admin);
        inSeconds = Math.Max(0, inSeconds);
        events.Broadcast(new ServerEvent { Shutdown = new ServerShutdown { InSeconds = inSeconds, Reason = reason } });
        feed.Publish(FeedTypes.ServerShutdown, new ShutdownData(inSeconds, reason));
        await Audit(actor, "shutdown", null, $"in {inSeconds}s: {reason}");
        log.LogWarning("Shutdown in {Seconds}s requested by {Actor}: {Reason}", inSeconds, actor.Name, reason);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(inSeconds) + TimeSpan.FromMilliseconds(200));
            lifetime.StopApplication();
        });
    }

    /// <summary>Archives every save and world object and forgets the world; everyone is kicked. The next admin login creates a new world.</summary>
    public async Task RecreateWorld(Actor actor)
    {
        actor.Require(Role.Admin);
        string archive = Path.Combine(data.Archive, $"world-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(archive);

        foreach (var session in sessions.All)
        {
            events.SendTo(session.PlayerId, new ServerEvent { Notice = new Notice { Text = "The world is being recreated; please reconnect later" } });
            sessions.End(session.Token);
        }
        foreach (long playerId in await db.SaveVersions.Select(v => v.PlayerId).Distinct().ToListAsync())
        {
            await saves.Archive(playerId, Path.Combine(archive, playerId.ToString()));
            await raids.CancelAllOf(playerId, "world recreated");
            await incidents.CancelAllOf(playerId, "world recreated");
            await shop.ClearOf(playerId);
            await visits.EndAllOf(playerId);
        }
        await db.WorldObjects.ExecuteDeleteAsync();
        await db.World.ExecuteDeleteAsync();
        await Audit(actor, "recreateworld", null, archive);
        log.LogWarning("World recreated by {Actor}; old data in {Archive}", actor.Name, archive);
    }

    /// <summary>Newest first; <paramref name="before"/> pages back to entries with a smaller id.</summary>
    public Task<List<AdminAction>> ListActions(Actor actor, int limit = 100, long? before = null)
    {
        actor.Require(Role.Moderator);
        var query = db.AdminActions.AsNoTracking();
        if (before is { } b) query = query.Where(a => a.Id < b);
        return query.OrderByDescending(a => a.Id).Take(limit).ToListAsync();
    }

    private async Task RemoveObjectsOf(long playerId)
    {
        var objects = await db.WorldObjects.Where(o => o.OwnerPlayerId == playerId).ToListAsync();
        db.WorldObjects.RemoveRange(objects);
        await db.SaveChangesAsync();
        foreach (var o in objects) events.Broadcast(new ServerEvent { WorldObjectRemoved = new WorldObjectRemoved { ObjectId = o.Id } });
        string owner = await db.Players.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.Name).FirstOrDefaultAsync() ?? "";
        foreach (var o in objects.Where(o => o.Type == WorldObjectTypes.Settlement))
            feed.Publish(FeedTypes.ColonyRemoved, new ColonyData(new FeedPlayer(playerId, owner), o.Name, null));
    }

    /// <summary>Defs forbidden in sends and lots, sorted.</summary>
    public IReadOnlyList<string> ListForbiddenDefs(Actor actor)
    {
        actor.Require(Role.Moderator);
        return forbidden.All;
    }

    /// <summary>Replaces the forbidden list; open lots holding such defs stay open.</summary>
    public async Task<IReadOnlyList<string>> SetForbiddenDefs(Actor actor, IEnumerable<string> defs)
    {
        actor.Require(Role.Admin);
        var list = await forbiddenStore.Set(defs);
        await Audit(actor, "forbidden-defs", null, list.Count == 0 ? "none" : string.Join(", ", list));
        return list;
    }

    private Task Audit(Actor actor, string action, long? targetAccountId, string details) => audit.Write(actor, action, targetAccountId, details);
}
