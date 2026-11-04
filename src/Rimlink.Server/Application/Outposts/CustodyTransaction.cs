using System.Data;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class PawnCustodyBridge
{
    public sealed record CustodyRoute(long Actor, long Recipient, PawnList Pawns, CustodyTransferKind Kind,
        long SourceId, string RequestId, byte[] RequestIntent, bool FromEscrow, bool Mirror = false,
        byte[]? SourcePayload = null, PawnList? ResultPawns = null, long SourceAuthorizer = 0);

    /// <summary>Outer entry only: A → sorted owners → S before the existing service stages its real rows.</summary>
    public Task<CustodyTransaction> BeginOutgoingAsync(long actor, long recipient, PawnList pawns, CustodyTransferKind kind,
        string requestId, byte[] requestIntent, CancellationToken ct = default) => BeginRoutesAsync(
            [new(actor, recipient, pawns, kind, 0, requestId, requestIntent, false)], ct);

    public Task<CustodyTransaction> BeginSourceAsync(long actor, long recipient, PawnList pawns, CustodyTransferKind kind,
        long sourceId, string requestId, byte[] requestIntent, CancellationToken ct = default) => BeginRoutesAsync(
            [new(actor, recipient, pawns, kind, sourceId, requestId, requestIntent, true)], ct);

    public Task<CustodyTransaction> BeginRoutesAsync(IReadOnlyList<CustodyRoute> routes, CancellationToken ct = default) =>
        BeginRoutesCoreAsync(routes, null, allowAcquire: true, ct);

    private async Task<CustodyTransaction> BeginRoutesCoreAsync(IReadOnlyList<CustodyRoute> routes, OutpostLease? borrowed, bool allowAcquire, CancellationToken ct)
    {
        if (routes.Count is < 1 or > 64) throw Invalid("custody-route-batch-size");
        // Untagged legacy cleanup may already hold O/S (archive/reset). Its own SQLite transaction rechecks the
        // history boundary before it writes; never reacquire those non-reentrant locks from a callback.
        if (borrowed is null && routes.All(r => r.Pawns.Pawns.All(p => p.Registry is null)))
        {
            bool retained = false;
            foreach (long actor in routes.Select(r => r.Actor).Distinct()) retained |= await db.PawnCustody.AsNoTracking().AnyAsync(p => p.PlayerId == actor, ct);
            if (!retained)
            {
                var legacy = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                try
                {
                    foreach (long actor in routes.Select(r => r.Actor).Distinct())
                        if (await db.PawnCustody.AsNoTracking().AnyAsync(p => p.PlayerId == actor, ct)) throw Invalid("custody-history-boundary-changed");
                    return new(this, legacy, []);
                }
                catch { await legacy.DisposeAsync(); throw; }
            }
        }
        var held = new List<IDisposable>(); OutpostLease? lease = borrowed;
        try
        {
            long[] ownerIds = routes.SelectMany(r => new[] { r.Actor, r.Recipient, r.SourceAuthorizer }).Where(id => id > 0).Distinct().Order().ToArray();
            if (borrowed is null && allowAcquire && routes.Any(r => r.Pawns.Pawns.Count > 0))
            {
                foreach (long ownerId in ownerIds) held.Add(await owners.Hold(ownerId, ct));
                held.Add(await files.PerStoreLock.HoldAsync(ct));
            }
            var protectedRoutes = new List<CustodyRoute>();
            foreach (var route in routes.Where(r => r.Pawns.Pawns.Count > 0))
                if (route.Pawns.Pawns.Any(p => p.Registry is not null)
                    || await db.PawnCustody.AsNoTracking().AnyAsync(p => p.PlayerId == route.Actor, ct)) protectedRoutes.Add(route);
            if (protectedRoutes.Count == 0)
                return new(this, await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct), held);
            for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose(); held.Clear();
            if (lease is null)
            {
                if (!allowAcquire) throw Invalid("custody-history-boundary-changed");
                lease = await authority.AcquireAsync(ownerIds, ct);
            }
            foreach (long ownerId in ownerIds) if (!lease.CoversOwner(ownerId)) throw new InvalidOperationException("custody-owner-lease-required");
            string id = Guid.NewGuid().ToString("D");
            var parts = new List<CustodyTransferEvidence>();
            foreach (var route in protectedRoutes)
            {
                var latest = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == route.Actor).OrderByDescending(v => v.Number).FirstOrDefaultAsync(ct);
                var metadata = SaveStore.RecoveryOf(latest).Outposts;
                if (latest?.SnapshotId is null || latest.RawSha256 is null || !metadata.Present) throw Invalid("custody-canonical-snapshot-required");
                var evidence = new CustodyTransferEvidence { TransitionId = id, Kind = route.Kind, SourceId = route.SourceId,
                    ActorPlayerId = route.Actor, RecipientPlayerId = route.Recipient, ClientRequestId = route.RequestId,
                    Pawns = route.Pawns.Clone(), NonAuthoritativeMirror = route.Mirror, SourceRequestIntent = ByteString.CopyFrom(route.RequestIntent),
                    SourcePayload = ByteString.CopyFrom(route.SourcePayload ?? []), ResultPawns = route.ResultPawns?.Clone(), SourceAuthorizerPlayerId = route.SourceAuthorizer,
                    RemovalSnapshot = new() { Scope = metadata.Metadata.Scope.Clone(), SnapshotId = latest.SnapshotId, RawSha256 = ByteString.CopyFrom(latest.RawSha256) } };
                if (route.FromEscrow && route.Pawns.Pawns.FirstOrDefault(p => p.Registry is not null) is { } tagged)
                {
                    var current = await db.PawnCustody.AsNoTracking().SingleAsync(p => p.Id == tagged.Registry.PawnCustodyId, ct);
                    var original = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == current.FrontierTransitionId, ct);
                    evidence.SourceTransitionId = original.Id;
                    if (route.Kind == CustodyTransferKind.CustodyTransferLiveReturn && route.SourcePayload is null)
                        evidence.SourcePayload = Parts(CustodyTransferEvidence.Parser.ParseFrom(original.Intent))
                            .Single(p => p.Pawns.Pawns.Any(x => x.Registry?.PawnCustodyId == tagged.Registry.PawnCustodyId)).SourcePayload;
                    evidence.RemovalSnapshot = Parts(CustodyTransferEvidence.Parser.ParseFrom(original.Intent))
                        .Single(p => p.Pawns.Pawns.Any(x => x.Registry?.PawnCustodyId == tagged.Registry.PawnCustodyId)).RemovalSnapshot.Clone();
                }
                parts.Add(evidence);
            }
            var root = parts[0]; root.RelatedTransfers.Add(parts.Skip(1));
            if (root.CalculateSize() > Rimlink.Protocol.Limits.MaxMessageBytes) throw Invalid("custody-route-batch-size");
            var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
            var transaction = await authority.BeginHeldAsync(lease, new(id, head.WorldId, root.ActorPlayerId,
                root.RemovalSnapshot.Scope.SaveGeneration, head.RestoreBoundary, "CustodyTransfer", "player:" + (root.SourceAuthorizerPlayerId > 0 ? root.SourceAuthorizerPlayerId : root.ActorPlayerId),
                new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.Empty), ct);
            return new(this, transaction, lease, root, ownsLease: borrowed is null);
        }
        catch
        {
            if (lease is not null && borrowed is null) await lease.DisposeAsync();
            for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose();
            throw;
        }
    }

    internal static IEnumerable<CustodyTransferEvidence> Parts(CustodyTransferEvidence root)
    {
        if (root.RelatedTransfers.Count > 63 || root.RelatedTransfers.Any(p => p.RelatedTransfers.Count > 0 || p.TransitionId != root.TransitionId))
            throw new InvalidDataException("custody-route-batch-shape");
        return new[] { root }.Concat(root.RelatedTransfers);
    }

    public sealed class CustodyTransaction : IAsyncDisposable
    {
        private readonly PawnCustodyBridge bridge;
        private readonly IDbContextTransaction? legacy;
        private readonly OutpostAuthority.OutpostDomainTransaction? domain;
        private readonly OutpostLease? lease;
        private readonly List<IDisposable> held = [];
        private readonly CustodyTransferEvidence? evidence;
        private readonly CustodyTransaction? parent;
        private readonly bool ownsLease;
        private readonly List<Func<Task>> afterCommit = [];
        private bool committed;
        internal CustodyTransaction(PawnCustodyBridge bridge, IDbContextTransaction legacy, List<IDisposable> held)
        { this.bridge = bridge; this.legacy = legacy; this.held = held; }
        internal CustodyTransaction(PawnCustodyBridge bridge, OutpostAuthority.OutpostDomainTransaction domain, OutpostLease lease, CustodyTransferEvidence evidence, bool ownsLease = true)
        { this.bridge = bridge; this.domain = domain; this.lease = lease; this.evidence = evidence; this.ownsLease = ownsLease; }
        private CustodyTransaction(CustodyTransaction parent) { this.parent = parent; bridge = parent.bridge; }
        public CustodyTransaction Borrow() => new(this);
        public bool HasEvidence => parent?.HasEvidence ?? evidence is not null;
        public async Task ValidateAsync(CancellationToken ct = default)
        {
            if (parent is not null) { await parent.ValidateAsync(ct); return; }
            if (evidence is not null) foreach (var part in Parts(evidence)) await bridge.ValidateRouteHeldAsync(lease!, part, ct);
        }
        public void Bind(long sourceId, params long[] parcelIds)
        { if (parent is not null) parent.Bind(sourceId, parcelIds); else if (evidence is not null) BindPart(evidence, sourceId, parcelIds); }
        public void BindFor(long actor, CustodyTransferKind kind, long sourceId, params long[] parcelIds)
        {
            if (parent is not null) { parent.BindFor(actor, kind, sourceId, parcelIds); return; }
            if (evidence is null) return;
            foreach (var part in Parts(evidence).Where(p => p.ActorPlayerId == actor && p.Kind == kind)) BindPart(part, sourceId, parcelIds);
        }
        public void BindRoute(long actor, long recipient, CustodyTransferKind kind, long sourceId, params long[] parcelIds)
        {
            if (parent is not null) { parent.BindRoute(actor, recipient, kind, sourceId, parcelIds); return; }
            if (evidence is null) return;
            foreach (var part in Parts(evidence).Where(p => p.ActorPlayerId == actor && p.RecipientPlayerId == recipient && p.Kind == kind)) BindPart(part, sourceId, parcelIds);
        }
        private static void BindPart(CustodyTransferEvidence part, long sourceId, long[] parcelIds)
        { part.SourceId = sourceId; part.ParcelIds.Clear(); part.ParcelIds.Add(parcelIds); }
        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (parent is not null) return;
            if (legacy is not null) await legacy.CommitAsync(ct);
            else
            {
                foreach (var part in Parts(evidence!)) await bridge.StageTransferHeldAsync(lease!, part, ct);
                await domain!.CompleteAsync(ByteString.CopyFrom(evidence!.ToByteArray()), ct);
            }
            committed = true;
            foreach (var action in afterCommit) await action();
        }
        public async Task AfterCommitAsync(Func<Task> action)
        {
            if (parent is not null) await parent.AfterCommitAsync(action);
            else if (committed) await action();
            else afterCommit.Add(action);
        }
        public async ValueTask DisposeAsync()
        {
            if (parent is not null) return;
            try { if (domain is not null) await domain.DisposeAsync(); if (legacy is not null) await legacy.DisposeAsync(); }
            finally { if (ownsLease && lease is not null) await lease.DisposeAsync(); for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
        }
    }
}
