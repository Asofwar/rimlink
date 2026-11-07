using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Application.Saves;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;

namespace Rimlink.Server.Application.Integrity;

/// <summary>
/// Runs after every stored upload (integrity spec §3–§5): a dev-mode save of a player not allowed to use it, or a wealth jump,
/// alerts admins (audit as <c>system</c>, feed <c>integrity.alert</c>) and may freeze the player's economy. A frozen player
/// raises no new alerts, so autosaves do not flood the audit.
/// </summary>
public sealed class IntegrityService(RimlinkDbContext db, PlayerFreeze freeze, AdminFeed feed, IOptions<IntegrityOptions> options, ILogger<IntegrityService> log)
{
    public const string Kind_DevMode = "devmode", Kind_WealthWarn = "wealth-warn", Kind_WealthFreeze = "wealth-freeze";

    public async Task OnStored(long playerId, SaveVersion? baseVersion, SaveVersion stored)
    {
        var player = await db.Players.AsNoTracking().FirstAsync(p => p.Id == playerId);
        if (player.EconomyFrozenAt is not null) return;
        var role = await db.Accounts.AsNoTracking().Where(a => a.Id == player.AccountId).Select(a => a.Role).FirstAsync();
        var o = options.Value;

        if (stored.DevMode && !o.DevModeAllowed(role))
        {
            string reason = $"dev mode was on in save v{stored.Number}";
            await freeze.Set(playerId, true, reason);
            await Alert(player, "integrity-devmode", Kind_DevMode, stored.Number, null, frozen: true, reason);
            return;
        }

        if (baseVersion is null) return;
        long imported = stored.RecoveryVersion == 0
            ? await ImportedBetween(playerId, baseVersion.GameTick, stored.GameTick)
            : await ImportedReceipts(playerId, baseVersion, stored);
        var result = WealthCheck.Evaluate(baseVersion.WealthTotal, baseVersion.GameTick, stored.WealthTotal, stored.GameTick, imported, o, role == Storage.Entities.Role.Admin);
        if (result is null || result.Verdict == WealthVerdict.None) return;

        string details = string.Create(CultureInfo.InvariantCulture,
            $"save v{stored.Number}: net {result.Net} in {result.Days:0.##} days = {result.Rate}/day (imported {imported})");
        if (result.Verdict == WealthVerdict.Freeze)
        {
            await freeze.Set(playerId, true, string.Create(CultureInfo.InvariantCulture, $"wealth +{result.Rate:N0}/day in save v{stored.Number}"));
            await Alert(player, "integrity-wealth", Kind_WealthFreeze, stored.Number, result, frozen: true, details);
        }
        else await Alert(player, "integrity-wealth", Kind_WealthWarn, stored.Number, result, frozen: false, details);
    }

    /// <summary>
    /// Silver worth of what reached the player's map by parcel with a delivery tick in (<paramref name="fromTickExclusive"/>,
    /// <paramref name="toTickInclusive"/>]. A parcel's silver is credited to the wallet on delivery, except a withdrawal's, which
    /// lands as stacks; the client saves right after a delivery, so a delivery at the base save's tick is already in it.
    /// </summary>
    public async Task<long> ImportedBetween(long playerId, long fromTickExclusive, long toTickInclusive)
    {
        var parcels = await db.Parcels.AsNoTracking()
            .Where(p => p.ToPlayerId == playerId && p.DeliveredAt != null && p.DeliveredTick > fromTickExclusive && p.DeliveredTick <= toTickInclusive)
            .ToListAsync();
        return PhysicalValue(parcels);
    }

    private async Task<long> ImportedReceipts(long playerId, SaveVersion before, SaveVersion after)
    {
        var previous = SaveStore.RecoveryOf(before); var current = SaveStore.RecoveryOf(after);
        static IEnumerable<long> Protected(SaveRecoveryData recovery) => recovery.Outposts.Metadata.Receipts
            .Where(r => r.DeliveryAttemptId.Length > 0).SelectMany(r => r.ParcelIds);
        var old = previous.ParcelIds.Concat(Protected(previous)).ToHashSet();
        var accepted = Protected(current).ToHashSet();
        var added = current.ParcelIds.Concat(accepted).Where(id => !old.Contains(id)).Distinct().ToArray();
        if (added.Length == 0) return 0;
        var links = await db.OutpostParcelLinks.AsNoTracking().Where(p => p.PlayerId == playerId && added.Contains(p.ParcelId)).ToDictionaryAsync(p => p.ParcelId);
        var parcels = await db.Parcels.AsNoTracking().Where(p => p.ToPlayerId == playerId && added.Contains(p.Id)
            && (p.DeliveryRecoveryVersion == 0 || p.DeliveredAt == null || p.DeliveredGeneration == after.SaveGeneration)).ToDictionaryAsync(p => p.Id);
        decimal value = PhysicalValue(parcels.Values.Where(p => !links.ContainsKey(p.Id)));
        foreach (var link in links.Values.Where(l => accepted.Contains(l.ParcelId) && l.AcceptedSnapshotId != null
            && l.SaveGeneration == after.SaveGeneration && l.WorldId == current.Outposts.Metadata.Scope?.WorldInstanceId))
        {
            if (OutpostParcelPayload.Parser.ParseFrom(link.Payload).ManifestId.Length > 0) value += link.ValueX100 / 100m;
            // Older ordinary handoff links stored only pawn value; their bound parcel retains all physical goods.
            else if (parcels.TryGetValue(link.ParcelId, out var parcel)) value += ServerWealth.PhysicalValueX100(parcel) / 100m;
        }
        return value <= 0 ? 0 : value >= long.MaxValue ? long.MaxValue : (long)decimal.Floor(value);
    }

    private static long PhysicalValue(IEnumerable<Parcel> parcels)
    {
        long x100 = 0, silver = 0;
        foreach (var p in parcels)
        {
            if (p.Kind == ParcelKindDb.Withdraw) silver += p.Silver;
            if (p.Items.Length > 0) x100 += ItemList.Parser.ParseFrom(p.Items).Items.Sum(i => i.MarketValueX100 * i.Count);
            if (p.Pawns.Length > 0) x100 += PawnList.Parser.ParseFrom(p.Pawns).Pawns.Sum(b => b.MarketValueX100);
        }
        return silver + x100 / 100;
    }

    private async Task Alert(Player player, string action, string kind, int version, WealthResult? result, bool frozen, string details)
    {
        db.AdminActions.Add(new AdminAction
        {
            ActorAccountId = Admin.Actor.System.AccountId, ActorName = Admin.Actor.System.Name, Action = action,
            TargetAccountId = player.AccountId, Details = details, At = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        log.LogWarning("Integrity: {Kind} for player {PlayerId} ({Name}): {Details}{Frozen}", kind, player.Id, player.Name, details, frozen ? "; economy frozen" : "");
        feed.Publish(FeedTypes.IntegrityAlert,
            new IntegrityAlertData(new FeedPlayer(player.Id, player.Name), kind, version, result?.Net, result?.Days, result?.Rate, frozen));
    }
}
