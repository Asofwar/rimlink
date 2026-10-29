using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Admin;

/// <summary>A directory row. <see cref="SteamId"/> is text: 17-digit SteamIDs exceed JavaScript's safe integers.</summary>
public sealed record PlayerRow(long Id, long AccountId, string Name, string FactionName, string Role, string? SteamId, string? Login, bool Online, DateTime? LastSeenAt, DateTime CreatedAt, DateTime? MutedUntil, bool Peaceful, bool Banned);
public sealed record PlayerPage(int Total, List<PlayerRow> Items);
public sealed record SettlementRow(long Id, int Tile, string Name);
public sealed record SaveRow(int Version, long Size, long GameTick, DateTime CreatedAt);
public sealed record BanRow(long Id, string Reason, DateTime? ExpiresAt);
public sealed record SessionRow(string Ip, DateTime Since);
public sealed record PlayerCard(PlayerRow Player, string? LastIp, SessionRow? Session, List<SettlementRow> Settlements, long WalletBalance, long WealthTotal, DateTime? FirstSaveAt, SaveRow? CurrentSave, List<BanRow> ActiveBans, bool VisitsClosed, DateTime? PeacefulOffAt);
public sealed record ChatLine(long Id, DateTime At, long? FromPlayerId, string? FromName, string Kind, string Text);

/// <summary>Read-only views for the admin panel and integrations: player directory, player card, public chat history. No audit.</summary>
public sealed class AdminDirectory(RimlinkDbContext db, SessionRegistry sessions, BanService bans, WalletService wallet)
{
    /// <summary>
    /// Searches every player, online or not. <paramref name="full"/> = false (a read-only token) matches only <c>#id</c> and the
    /// name/faction substring, never the login or SteamID.
    /// </summary>
    public async Task<PlayerPage> Search(string? q, int limit, int offset, bool full)
    {
        if (limit is < 1 or > 200) throw new RpcException(new Status(StatusCode.InvalidArgument, "limit must be 1-200"));
        if (offset < 0) throw new RpcException(new Status(StatusCode.InvalidArgument, "offset must not be negative"));

        var query = db.Players.AsNoTracking().Include(p => p.Account).AsQueryable();
        string term = q?.Trim() ?? "";
        bool byText = false;
        if (term.Length > 0)
        {
            if (term[0] == '#' && long.TryParse(term[1..], out long id)) query = query.Where(p => p.Id == id);
            else if (full && term.Length == 17 && term.All(char.IsAsciiDigit)) query = query.Where(p => p.Account.SteamId == long.Parse(term));
            else byText = true;
        }

        // Online state lives in the session registry, so sorting and paging happen here (players per server: thousands at most).
        // The name/faction match is in memory too: SQLite's lower() folds ASCII only, so Cyrillic names would never match.
        var players = await query.ToListAsync();
        if (byText)
            players = players.Where(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || p.FactionName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (full && p.Account.Login == term)).ToList();
        var banned = await BannedSets();
        var online = sessions.All.Select(s => s.PlayerId).ToHashSet();
        var rows = players
            .Select(p => Row(p, online.Contains(p.Id), banned))
            .OrderByDescending(r => term.Length > 0 && string.Equals(r.Name, term, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.Online)
            .ThenByDescending(r => r.LastSeenAt)
            .ThenBy(r => r.Id)
            .ToList();
        return new PlayerPage(rows.Count, rows.Skip(offset).Take(limit).ToList());
    }

    public async Task<PlayerCard> Card(long playerId)
    {
        var player = await db.Players.AsNoTracking().Include(p => p.Account).FirstOrDefaultAsync(p => p.Id == playerId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"no player with id {playerId}"));
        var session = sessions.ByPlayer(playerId);
        var banned = await BannedSets();
        var activeBans = (await bans.ListActive())
            .Where(b => b.AccountId == player.AccountId || (player.Account.SteamId is { } steam && b.SteamId == steam))
            .Select(b => new BanRow(b.Id, b.Reason, b.ExpiresAt)).ToList();
        var settlements = await db.WorldObjects.AsNoTracking()
            .Where(o => o.OwnerPlayerId == playerId && o.Type == WorldObjectTypes.Settlement)
            .Select(o => new SettlementRow(o.Id, o.Tile, o.Name)).ToListAsync();
        var current = await db.SaveVersions.AsNoTracking()
            .Where(v => v.PlayerId == playerId && v.IsCurrent)
            .Select(v => new SaveRow(v.Number, v.Size, v.GameTick, v.CreatedAt)).FirstOrDefaultAsync();

        return new PlayerCard(
            Row(player, session is not null, banned),
            player.LastIp,
            session is null ? null : new SessionRow(session.Ip, session.StartedAt),
            settlements,
            await wallet.Balance(playerId),
            player.WealthTotal,
            player.FirstSaveAt,
            current,
            activeBans,
            player.VisitsClosed,
            player.PeacefulOffAt);
    }

    /// <summary>
    /// Public channel only. Without a cursor or with <paramref name="before"/>: newest first. With <paramref name="after"/>: ascending,
    /// the forward cursor for bridges and polling.
    /// </summary>
    public async Task<List<ChatLine>> Chat(long? playerId, long? before, long? after, int limit)
    {
        if (limit is < 1 or > 500) throw new RpcException(new Status(StatusCode.InvalidArgument, "limit must be 1-500"));
        if (before is not null && after is not null) throw new RpcException(new Status(StatusCode.InvalidArgument, "use either before or after, not both"));

        var query = db.ChatMessages.AsNoTracking().Where(m => m.ToPlayerId == null);
        if (playerId is { } from) query = query.Where(m => m.FromPlayerId == from);
        if (after is { } a) query = query.Where(m => m.Id > a).OrderBy(m => m.Id);
        else query = (before is { } b ? query.Where(m => m.Id < b) : query).OrderByDescending(m => m.Id);

        var messages = await query.Take(limit).ToListAsync();
        var senders = messages.Where(m => m.FromPlayerId is not null).Select(m => m.FromPlayerId!.Value).Distinct().ToList();
        var names = await db.Players.AsNoTracking().Where(p => senders.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        return messages.Select(m => new ChatLine(m.Id, m.SentAt, m.FromPlayerId, m.FromPlayerId is { } f && names.TryGetValue(f, out var n) ? n : null, m.Kind.ToString().ToLowerInvariant(), m.Text)).ToList();
    }

    /// <summary>Player names by account id, for the audit log and the ban list.</summary>
    public async Task<Dictionary<long, string>> NamesByAccount(IEnumerable<long> accountIds)
    {
        var ids = accountIds.Distinct().ToList();
        return await db.Players.AsNoTracking().Where(p => ids.Contains(p.AccountId)).ToDictionaryAsync(p => p.AccountId, p => p.Name);
    }

    private async Task<(HashSet<long> Accounts, HashSet<long> SteamIds)> BannedSets()
    {
        var active = await bans.ListActive();
        return (active.Where(b => b.AccountId is not null).Select(b => b.AccountId!.Value).ToHashSet(),
                active.Where(b => b.SteamId is not null).Select(b => b.SteamId!.Value).ToHashSet());
    }

    private static PlayerRow Row(Player p, bool online, (HashSet<long> Accounts, HashSet<long> SteamIds) banned) => new(
        p.Id, p.AccountId, p.Name, p.FactionName, p.Account.Role.ToString().ToLowerInvariant(), p.Account.SteamId?.ToString(), p.Account.Login,
        online, p.Account.LastSeenAt, p.Account.CreatedAt, p.MutedUntil, p.Peaceful,
        banned.Accounts.Contains(p.AccountId) || (p.Account.SteamId is { } steam && banned.SteamIds.Contains(steam)));
}
