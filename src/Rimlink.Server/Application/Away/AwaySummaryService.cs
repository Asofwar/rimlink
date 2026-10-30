using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Lot = Rimlink.Server.Storage.Entities.Lot;
using Raid = Rimlink.Server.Storage.Entities.Raid;
using MessageKind = Rimlink.Server.Storage.Entities.MessageKind;

namespace Rimlink.Server.Application.Away;

/// <summary>Builds the "while you were away" summary of a session from existing tables, strictly after <see cref="Session.AwaySince"/> (spec §3).</summary>
public sealed partial class AwaySummaryService(RimlinkDbContext db)
{
    public const int ListCap = 10;
    public const int MentionCap = 5;
    private const int MaxLineLength = 200;

    public async Task<AwaySummary> Build(Session session)
    {
        var summary = new AwaySummary { ColonyRestore = session.ColonyRestore };
        if (session.AwaySince is not { } since) return summary;
        summary.Since = Stamp(since);
        long me = session.PlayerId;

        var raidsOnMe = await db.Raids.AsNoTracking()
            .Where(r => r.TargetPlayerId == me && (r.CreatedAt > since
                || (r.ResolvedAt > since && (r.State == RaidStateDb.Cancelled || r.State == RaidStateDb.Expired))))
            .ToListAsync();
        var myRaids = await db.Raids.AsNoTracking()
            .Where(r => r.AttackerPlayerId == me && r.ResolvedAt > since
                && (r.State == RaidStateDb.Resolved || r.State == RaidStateDb.Cancelled || r.State == RaidStateDb.Expired))
            .ToListAsync();
        var visits = await db.Visits.AsNoTracking().Where(v => v.HostPlayerId == me && v.StartedAt > since).ToListAsync();
        var parcels = await db.Parcels.AsNoTracking().Where(p => p.ToPlayerId == me && p.CreatedAt > since).ToListAsync();
        var lots = await db.Lots.AsNoTracking().Where(l => l.SellerPlayerId == me && l.Status == LotStatusDb.Sold && l.ClosedAt > since).ToListAsync();
        var actions = await db.AdminActions.AsNoTracking()
            .Where(a => a.TargetAccountId == session.AccountId && a.At > since && (a.ActorAccountId == 0 || a.ActorAccountId != session.AccountId))
            .ToListAsync();

        // Shop sales: the host's "sale" wallet entries for its shop rows (a sale's buyer is not stored), and the things taken.
        // Lot sales write the same kind with the lot id as reference, and lot and shop ids can be equal: the one entry nearest
        // to each sold lot's close is that lot's, the rest are the shop's.
        var shopIds = await db.ShopItems.AsNoTracking().Where(s => s.HostPlayerId == me).Select(s => s.Id).ToListAsync();
        var sales = await db.WalletEntries.AsNoTracking().Where(e => e.PlayerId == me && e.Kind == "sale" && e.At > since).ToListAsync();
        foreach (var lot in lots)
        {
            var entry = sales.Where(e => e.RefId == lot.Id).OrderBy(e => Math.Abs((e.At - lot.ClosedAt!.Value).Ticks)).FirstOrDefault();
            if (entry is not null) sales.Remove(entry);
        }
        summary.ShopSilver = sales.Where(e => shopIds.Contains(e.RefId)).Sum(e => e.Delta);
        summary.ShopItemsSold = await db.PendingRemovals.AsNoTracking().Where(r => r.HostPlayerId == me && r.CreatedAt > since).SumAsync(r => r.Count);

        var name = session.PlayerName;
        // Like the chat history: public lines of players this player blocked are not shown.
        var blocked = (await db.PlayerBlocks.AsNoTracking().Where(b => b.BlockerPlayerId == me).Select(b => b.BlockedPlayerId).ToListAsync()).ToHashSet();
        var lines = (await db.ChatMessages.AsNoTracking()
                .Where(m => m.SentAt > since && m.FromPlayerId != me
                    && (m.ToPlayerId == me || (m.ToPlayerId == null && (m.Kind == MessageKind.Player || m.Kind == MessageKind.External))))
                .ToListAsync())
            .Where(m => m.ToPlayerId == me || (MentionMatcher.Mentions(m.Text, name) && !(m.FromPlayerId is { } from && blocked.Contains(from))))
            .ToList();

        var ids = raidsOnMe.Select(r => r.AttackerPlayerId)
            .Concat(myRaids.Select(r => r.TargetPlayerId))
            .Concat(visits.Select(v => v.VisitorPlayerId))
            .Concat(parcels.Where(p => p.FromPlayerId is not null).Select(p => p.FromPlayerId!.Value))
            .Concat(lots.Where(l => l.BuyerPlayerId is not null).Select(l => l.BuyerPlayerId!.Value))
            .Concat(lines.Where(m => m.FromPlayerId is not null).Select(m => m.FromPlayerId!.Value))
            .Distinct().ToList();
        var players = await db.Players.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ToRef());
        PlayerRef? Ref(long? id) => id is { } v && players.TryGetValue(v, out var r) ? r : null;

        summary.RaidsOnMeMore = Fill(summary.RaidsOnMe, raidsOnMe, r => r.ResolvedAt is { } at && r.State is RaidStateDb.Cancelled or RaidStateDb.Expired ? at : r.CreatedAt,
            (r, at) => ToAway(r, Ref(r.AttackerPlayerId), at), ListCap);
        summary.MyRaidsMore = Fill(summary.MyRaids, myRaids, r => r.ResolvedAt!.Value, (r, at) => ToAway(r, Ref(r.TargetPlayerId), at), ListCap);
        summary.VisitsMore = Fill(summary.Visits, visits, v => v.StartedAt, (v, at) => new AwayVisit { Visitor = Ref(v.VisitorPlayerId), At = Stamp(at) }, ListCap);
        summary.ParcelsMore = Fill(summary.Parcels, parcels, p => p.CreatedAt,
            (p, at) => new AwayParcel { Kind = Snake(p.Kind.ToString()), From = Ref(p.FromPlayerId), Silver = p.Silver, At = Stamp(at) }, ListCap);
        summary.LotSalesMore = Fill(summary.LotSales, lots, l => l.ClosedAt!.Value,
            (l, at) => new AwaySale { Summary = SummaryOf(l), Price = l.Price, Buyer = Ref(l.BuyerPlayerId), At = Stamp(at) }, ListCap);
        summary.MentionsMore = Fill(summary.Mentions, lines, m => m.SentAt, (m, at) => new AwayLine
        {
            From = Ref(m.FromPlayerId),
            Author = m.ExternalAuthor ?? "",
            Text = m.Text.Length > MaxLineLength ? m.Text[..MaxLineLength] : m.Text,
            Direct = m.ToPlayerId == me,
            At = Stamp(at),
        }, MentionCap);
        summary.AdminActionsMore = Fill(summary.AdminActions, actions, a => a.At,
            (a, at) => new AwayAdminAction { Action = a.Action, Actor = a.ActorName ?? "admin", Details = a.Details, At = Stamp(at) }, ListCap);
        return summary;
    }

    /// <summary>Adds the newest <paramref name="cap"/> rows to <paramref name="target"/>; returns how many were left out.</summary>
    private static int Fill<TRow, TOut>(Google.Protobuf.Collections.RepeatedField<TOut> target, List<TRow> rows, Func<TRow, DateTime> at, Func<TRow, DateTime, TOut> map, int cap)
    {
        foreach (var row in rows.OrderByDescending(at).Take(cap)) target.Add(map(row, at(row)));
        return Math.Max(0, rows.Count - cap);
    }

    private static AwayRaid ToAway(Raid r, PlayerRef? other, DateTime at)
    {
        var away = new AwayRaid { RaidId = r.Id, Other = other, State = r.State.ToString().ToLowerInvariant(), At = Stamp(at) };
        if (r.SummaryJson is not null) away.Summary = RaidSummary.Parser.ParseJson(r.SummaryJson);
        return away;
    }

    private static string SummaryOf(Lot l) =>
        LotSummary.Of(ItemList.Parser.ParseFrom(l.Items), l.Pawn is { Length: > 0 } pawn ? PawnBlob.Parser.ParseFrom(pawn) : null);

    private static Timestamp Stamp(DateTime at) => Timestamp.FromDateTime(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private static string Snake(string pascal) => CapitalInside().Replace(pascal, "_$1").ToLowerInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex CapitalInside();
}
