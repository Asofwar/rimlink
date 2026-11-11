using Microsoft.EntityFrameworkCore;
using Google.Protobuf;
using System.Text.Json.Serialization;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildRestoreMap<T>(T Before, T After);
internal sealed record GuildPendingDeliveryCut(string ClaimId, long ParcelId, byte[] PayloadDigest, byte[] RecipientScope);
internal sealed record GuildRestorePlan(int Version, GuildRestoreMap<GuildOutpostRow>[] Outposts,
    GuildRestoreMap<GuildCampaign>[] Campaigns, GuildRestoreMap<GuildBattle>[] Battles,
    GuildRestoreMap<GuildAcquisitionReservation>[] Acquisitions, GuildRestoreMap<GuildBattleSlot>[] Slots,
    GuildRestoreMap<GuildBattleControlGrant>[] Grants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuildPendingDeliveryCut[]? PendingDeliveries = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuildRestoreMap<GuildDeliveryAttempt>[]? RetiredAttempts = null);

internal static class GuildRestore
{
    internal const int MaxPendingDeliveries = 512;

    internal static Task<bool> PendingDeliverySlotsFullAsync(RimlinkDbContext db, CancellationToken ct) =>
        PendingDeliveries(db).Skip(MaxPendingDeliveries - 1).AnyAsync(ct);

    private static IOrderedQueryable<GuildParcelLink> PendingDeliveries(RimlinkDbContext db) =>
        db.Set<GuildParcelLink>().AsNoTracking().Where(link => link.AcceptedSnapshotId == null).OrderBy(link => link.ParcelId);
    internal static void ValidateShape(GuildRestorePlan plan)
    {
        if (plan.Version != 1 || !Bounded(plan.Outposts, 32, x => x.Id) || !Bounded(plan.Campaigns, 32, x => x.Id)
            || !Bounded(plan.Battles, 32, x => x.Id) || !Bounded(plan.Acquisitions, 32, x => x.Id)
            || !Bounded(plan.Slots, 32, x => x.Id) || !Bounded(plan.Grants, 64, x => x.Id)
            || plan.PendingDeliveries is { } deliveries && (deliveries.Length is < 1 or > MaxPendingDeliveries
                || deliveries.Any(d => d is null || d.ParcelId <= 0 || !Guid.TryParseExact(d.ClaimId, "D", out _)
                    || d.PayloadDigest is not { Length: 32 } || d.RecipientScope is not { Length: > 0 and <= 512 })
                || !deliveries.Select(d => d.ParcelId).SequenceEqual(deliveries.Select(d => d.ParcelId).Distinct().Order()))
            || plan.RetiredAttempts is { } retired && (retired.Length == 0 || !Bounded(retired, MaxPendingDeliveries, x => x.Id)
                || retired.Any(m => !Retires(m) || plan.PendingDeliveries?.Any(d => d.ParcelId == m.Before.ParcelId) != true)))
            throw new OutpostEvidenceUnavailableException("guild-restore-plan-invalid");
    }

    private static bool Bounded<T>(GuildRestoreMap<T>[]? maps, int maximum, Func<T, string> id) where T : class =>
        maps is not null && maps.Length <= maximum && maps.All(m => m is not null && m.Before is not null && m.After is not null
            && id(m.Before) == id(m.After)) && maps.Select(m => id(m.Before)).Distinct(StringComparer.Ordinal).Count() == maps.Length;

    internal static async Task<GuildRestorePlan> CreateAsync(RimlinkDbContext db, string world, string restoreId, DateTime cutUtc, CancellationToken ct)
    {
        await RequireSupportedSourceAsync(db, ct);
        var objects = await db.Set<GuildOutpostRow>().AsNoTracking().Where(x => x.WorldId == world).OrderBy(x => x.Id).Take(33).ToArrayAsync(ct);
        if (objects.Length > 32) throw new OutpostEvidenceUnavailableException("guild-restore-object-limit");
        var campaigns = await db.Set<GuildCampaign>().AsNoTracking().Where(x => x.WorldId == world && x.Active).OrderBy(x => x.Id).Take(33).ToArrayAsync(ct);
        var battles = await db.Set<GuildBattle>().AsNoTracking().Where(x => x.WorldId == world && x.Active).OrderBy(x => x.Id).Take(33).ToArrayAsync(ct);
        if (campaigns.Length > 32 || battles.Length > 32) throw new OutpostEvidenceUnavailableException("guild-restore-campaign-limit");
        var acquisitions = await db.Set<GuildAcquisitionReservation>().AsNoTracking().Where(x => x.Active && db.Set<GuildCampaign>().Any(c => c.Id == x.CampaignId && c.WorldId == world)).OrderBy(x => x.Id).Take(33).ToArrayAsync(ct);
        var slots = await db.Set<GuildBattleSlot>().AsNoTracking().Where(x => x.Active && db.Set<GuildCampaign>().Any(c => c.Id == x.CampaignId && c.WorldId == world)).OrderBy(x => x.Id).Take(33).ToArrayAsync(ct);
        var grants = await db.Set<GuildBattleControlGrant>().AsNoTracking().Where(x => x.Active && db.Set<GuildBattle>().Any(b => b.Id == x.BattleId && b.WorldId == world)).OrderBy(x => x.Id).Take(65).ToArrayAsync(ct);
        if (acquisitions.Length > 32 || slots.Length > 32 || grants.Length > 64) throw new OutpostEvidenceUnavailableException("guild-restore-reservation-limit");
        var pending = await (from link in PendingDeliveries(db)
            join claim in db.Set<GuildResourceClaim>().AsNoTracking() on link.ClaimId equals claim.Id
            orderby link.ParcelId
            select new { Claim = claim, Link = link }).Take(MaxPendingDeliveries + 1).ToArrayAsync(ct);
        if (pending.Length > MaxPendingDeliveries || pending.Any(p => p.Claim.WorldId != world))
            throw new OutpostEvidenceUnavailableException("guild-delivery-restore-cut-limit");
        var attempts = await db.Set<GuildDeliveryAttempt>().AsNoTracking().Where(a => a.Active).OrderBy(a => a.Id).Take(MaxPendingDeliveries + 1).ToArrayAsync(ct);
        if (attempts.Length > MaxPendingDeliveries) throw new OutpostEvidenceUnavailableException("guild-delivery-restore-cut-limit");
        return new(1, Map(objects, x =>
        {
            x.CheckpointUtc = cutUtc; x.ClockProcessId = restoreId; x.ClockTimestamp = 0; x.ClockFrequency = TimeSpan.TicksPerSecond;
            if (x.Phase == "PreparingBattle") x.Phase = "Active";
        }), Map(campaigns, x => { x.Active = false; x.Phase = "TechnicalAbort"; x.TerminalReason = "restore-boundary"; }),
            // The new authority boundary revokes transport, while the historical assignment still occupies the arena.
            Map(battles, _ => { }),
            Map(acquisitions, x => x.Active = false), Map(slots, x => x.Active = false), Map(grants, x => x.Active = false),
            pending.Length == 0 ? null : pending.Select(p => new GuildPendingDeliveryCut(p.Claim.Id, p.Link.ParcelId,
                p.Link.PayloadDigest, OriginalRecipient(p.Claim).ToByteArray())).ToArray(),
            attempts.Length == 0 ? null : Map(attempts, a => a.Active = false));
    }

    internal static async Task RequireSupportedSourceAsync(RimlinkDbContext db, CancellationToken ct)
    {
        if (await PendingDeliveries(db).Skip(MaxPendingDeliveries).AnyAsync(ct))
            throw new OutpostEvidenceUnavailableException("guild-delivery-restore-cut-limit");
        var deliveries = await db.Set<GuildDeliveryAttempt>().AsNoTracking().Where(a => a.Active || a.AcceptedSnapshotId != null).ToArrayAsync(ct);
        foreach (var recipient in deliveries.GroupBy(a => a.RecipientId))
        {
            var canonical = await db.SaveVersions.AsNoTracking().SingleOrDefaultAsync(s => s.PlayerId == recipient.Key && s.IsCurrent, ct);
            var metadata = SaveStore.RecoveryOf(canonical).Outposts.Metadata;
            if (canonical is null || metadata.GuildMetadata is null || recipient.Any(a => a.WorldId != metadata.Scope.WorldInstanceId
                || a.SaveGeneration != canonical.SaveGeneration))
                throw new OutpostEvidenceUnavailableException("guild-delivery-restore-canonical-receipt-missing");
            foreach (var attempt in recipient)
            {
                if (attempt.Active)
                {
                    if (!ExactBefore(canonical, attempt) || attempt.Receipt is not null || attempt.AcceptedSnapshotId is not null
                        || metadata.GuildMetadata.Receipts.Any(r => r.ParcelId == attempt.ParcelId))
                        throw new OutpostEvidenceUnavailableException("guild-delivery-restore-unqualified");
                }
                else if (!metadata.GuildMetadata.Receipts.Any(receipt => receipt.AttemptId == attempt.Id
                    && receipt.ToByteArray().AsSpan().SequenceEqual(attempt.Receipt)))
                    throw new OutpostEvidenceUnavailableException("guild-delivery-restore-canonical-receipt-missing");
            }
        }
        var active = await db.Set<GuildBattle>().AsNoTracking().Where(x => x.Active).Take(2).ToArrayAsync(ct);
        if (active.Length > 1 || active.Any(x => x.Phase is not ("AppliedAwaitingCleanup" or "CancelledAwaitingCleanup")))
            throw new OutpostEvidenceUnavailableException("guild-restore-active-battle-reconciliation-required");
        if (active.Length == 1) await GuildAuthorityEvidence.CleanupTargetAsync(db, active[0], ct);
    }

    internal static async Task<GuildColonyScope> DeliveryScopeAsync(RimlinkDbContext db, GuildResourceClaim claim, GuildParcelLink link,
        string boundary, CancellationToken ct)
    {
        var scope = OriginalRecipient(claim);
        if (scope.RestoreBoundary == boundary) return scope;
        var restore = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Kind == "BulkRestore"
            && t.WorldId == scope.WorldInstanceId && t.RestoreBoundary == boundary, ct);
        var plan = restore is null ? null : OutpostRestoreEvidence.Plan(restore);
        if (plan?.Guild?.PendingDeliveries?.Any(d => d.ClaimId == claim.Id && d.ParcelId == link.ParcelId
                && d.PayloadDigest.AsSpan().SequenceEqual(link.PayloadDigest)
                && d.RecipientScope.AsSpan().SequenceEqual(scope.ToByteArray())) != true)
            throw new OutpostEvidenceUnavailableException("guild-delivery-restore-scope-missing");
        scope.RestoreBoundary = boundary;
        return scope;
    }

    private static GuildColonyScope OriginalRecipient(GuildResourceClaim claim) => new()
    {
        WorldInstanceId = claim.WorldId, RestoreBoundary = claim.Boundary, PlayerId = claim.RecipientId,
        SaveGeneration = claim.SaveGeneration, SaveWriteEpoch = claim.SaveWriteEpoch
    };

    internal static bool ExactBefore(SaveVersion canonical, GuildDeliveryAttempt attempt) =>
        canonical.PlayerId == attempt.RecipientId && canonical.SaveGeneration == attempt.SaveGeneration
        && canonical.SaveWriteEpoch == attempt.SaveWriteEpoch && canonical.SnapshotId == attempt.BeforeSnapshotId
        && canonical.RawSha256 is { Length: 32 } hash && hash.AsSpan().SequenceEqual(attempt.BeforeSnapshotDigest);

    private static bool Retires(GuildRestoreMap<GuildDeliveryAttempt> map)
    {
        if (!map.Before.Active || map.Before.AcceptedSnapshotId is not null || map.Before.Receipt is not null) return false;
        var expected = OutpostRestoreEvidence.Decode<GuildDeliveryAttempt>(OutpostRestoreEvidence.Encode(map.Before));
        expected.Active = false;
        return OutpostRestoreEvidence.Same(expected, map.After);
    }

    internal static async Task ApplyAsync(RimlinkDbContext db, GuildRestorePlan plan, CancellationToken ct)
    {
        ValidateShape(plan);
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("guild-restore-held-transaction-required");
        await Apply(db, plan.Outposts, x => x.Id, ct); await Apply(db, plan.Campaigns, x => x.Id, ct);
        await Apply(db, plan.Battles, x => x.Id, ct); await Apply(db, plan.Acquisitions, x => x.Id, ct);
        await Apply(db, plan.Slots, x => x.Id, ct); await Apply(db, plan.Grants, x => x.Id, ct);
        if (plan.RetiredAttempts is { } attempts) await Apply(db, attempts, x => x.Id, ct);
    }

    internal static async Task<GuildRestoreMap<GuildOutpostRow>[]> ResumeAsync(RimlinkDbContext db, GuildOutpostRow[] sources, ClockSample sample, CancellationToken ct)
    {
        var maps = Map(sources, x =>
        {
            var next = OutpostClock.Advance(new(x.CheckpointUtc, x.ClockProcessId, x.ClockTimestamp, x.ClockFrequency), sample);
            x.CheckpointUtc = next.Utc; x.ClockProcessId = next.ProcessId; x.ClockTimestamp = next.Timestamp; x.ClockFrequency = next.Frequency;
        });
        await Apply(db, maps, x => x.Id, ct);
        return maps;
    }

    internal static void ValidateCompletion(GuildOutpostRow[] sources, GuildRestoreMap<GuildOutpostRow>[] maps, ClockSample cut)
    {
        var expected = Map(sources, x =>
        {
            var next = OutpostClock.Advance(new(x.CheckpointUtc, x.ClockProcessId, x.ClockTimestamp, x.ClockFrequency), cut);
            x.CheckpointUtc = next.Utc; x.ClockProcessId = next.ProcessId; x.ClockTimestamp = next.Timestamp; x.ClockFrequency = next.Frequency;
        });
        if (!OutpostRestoreEvidence.Same(expected, maps)) throw new OutpostEvidenceUnavailableException("guild-restore-completion-mismatch");
    }

    private static GuildRestoreMap<T>[] Map<T>(T[] rows, Action<T> mutate) => rows.Select(row =>
    {
        var before = OutpostRestoreEvidence.Decode<T>(OutpostRestoreEvidence.Encode(row));
        var after = OutpostRestoreEvidence.Decode<T>(OutpostRestoreEvidence.Encode(row));
        mutate(after); return new GuildRestoreMap<T>(before, after);
    }).ToArray();

    private static async Task Apply<T>(RimlinkDbContext db, GuildRestoreMap<T>[] maps, Func<T, string> key, CancellationToken ct) where T : class
    {
        foreach (var map in maps)
        {
            var row = await db.Set<T>().FindAsync([key(map.Before)], ct)
                ?? throw new OutpostEvidenceUnavailableException("guild-restore-row-missing");
            if (!OutpostRestoreEvidence.Same(row, map.Before)) throw new OutpostEvidenceUnavailableException("guild-restore-row-changed");
            db.Entry(row).CurrentValues.SetValues(map.After);
        }
    }
}
