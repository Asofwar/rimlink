using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using OutpostAuthority = Rimlink.Server.Application.Outposts.OutpostAuthority;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildGovernanceReceipt(string RequestId, byte[] Result);

public sealed class GuildLifecycle(RimlinkDbContext db, OutpostAuthority authority, SaveFiles files, LiveStateLock live)
{
    internal static string OperationRequestId(long player, string request) => new Guid(SHA256.HashData(
        Encoding.UTF8.GetBytes("Rimlink.GuildGovernance/v1\0" + player.ToString(CultureInfo.InvariantCulture) + "\0" + request)).AsSpan(0, 16)).ToString("D");

    internal async Task<GuildSocialTransaction> BeginSocialAsync(long player, CommunityRequest request, CancellationToken ct)
    {
        bool governance = request.Action is CommunityAction.AllianceCreate or CommunityAction.AllianceLeave
            or CommunityAction.AllianceTransfer or CommunityAction.AllianceDisband
            || request.Action == CommunityAction.InviteAccept && await db.Set<CommunityInvitation>().AnyAsync(i => i.Id == request.InviteId && i.AllianceId != 0, ct);
        if (!governance) return new(db, await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct));
        IDisposable? storage = null, heldLive = null;
        OutpostLease? lease = null;
        OutpostAuthority.OutpostDomainTransaction? transaction = null;
        try
        {
            // Serialize legacy governance with V3 activation, without ever requesting A from under S.
            storage = await files.PerStoreLock.HoldAsync(ct);
            if (authority.VerifiedDomainFormat != OutpostDomainFormat.V3)
            {
                heldLive = await live.Hold();
                return new(db, await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct), storage, heldLive);
            }
            storage.Dispose(); storage = null;
            lease = await authority.AcquireAsync([player], ct);
            heldLive = await live.Hold();
            if ((await authority.CheckHeldAsync(lease, ct)).State != OutpostStartupState.Ready
                || authority.VerifiedDomainFormat != OutpostDomainFormat.V3) throw new OutpostEvidenceUnavailableException("guild-governance-authority-unavailable");
            var root = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
            string id = Guid.NewGuid().ToString("D");
            transaction = await authority.BeginHeldAsync(lease, new(id, root.WorldId, player, 0, root.RestoreBoundary,
                "GuildGovernance", "player:" + player.ToString(CultureInfo.InvariantCulture), new(root.HeadId, ByteString.CopyFrom(root.HeadDigest)), ByteString.Empty), ct);
            return new(db, transaction, lease, heldLive, player, request.Clone(), id);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            heldLive?.Dispose();
            if (lease is not null) await lease.DisposeAsync();
            storage?.Dispose();
            throw;
        }
    }

    internal sealed class GuildSocialTransaction : IAsyncDisposable
    {
        private readonly RimlinkDbContext db;
        private readonly IDbContextTransaction? ordinary;
        private readonly OutpostAuthority.OutpostDomainTransaction? domain;
        private readonly IDisposable? storage, live;
        private readonly OutpostLease? lease;
        private readonly long player;
        private readonly CommunityRequest? request;
        private readonly string? transition;

        internal GuildSocialTransaction(RimlinkDbContext db, IDbContextTransaction ordinary, IDisposable? storage = null, IDisposable? live = null)
        { this.db = db; this.ordinary = ordinary; this.storage = storage; this.live = live; }
        internal GuildSocialTransaction(RimlinkDbContext db, OutpostAuthority.OutpostDomainTransaction domain,
            OutpostLease lease, IDisposable live, long player, CommunityRequest request, string transition)
        { this.db = db; this.domain = domain; this.lease = lease; this.live = live; this.player = player; this.request = request; this.transition = transition; }

        internal async Task RequireDisbandSafeAsync(long alliance, CancellationToken ct)
        {
            if (domain is null) return;
            // Safe issuance is required before neutralization; unresolved assets never disappear with membership.
            if (await db.Set<GuildOutpostRow>().AnyAsync(o => o.AllianceId == alliance, ct)
                || await db.Set<GuildResourceObligation>().AnyAsync(o => o.AllianceId == alliance && o.RemainingCount > 0, ct)
                || await db.Set<GuildResourceClaim>().AnyAsync(c => c.AllianceId == alliance && c.State != "Accepted", ct)
                || await db.Set<GuildCampaign>().AnyAsync(c => c.Active && (c.AttackerAllianceId == alliance || c.DefenderAllianceId == alliance), ct))
                throw new OutpostEvidenceUnavailableException("guild-disband-requires-asset-reconciliation");
        }

        internal async Task CommitAsync(CommunityReceipt receipt, CancellationToken ct)
        {
            if (ordinary is not null) { await ordinary.CommitAsync(ct); return; }
            if (request!.Action == CommunityAction.AllianceLeave)
                foreach (var grant in await db.Set<GuildBattleControlGrant>().Where(g => g.PlayerId == player && g.Active).ToArrayAsync(ct)) grant.Active = false;
            var operation = new GuildCaptureOperation { Id = Guid.NewGuid().ToString("D"), ActorId = player,
                RequestId = OperationRequestId(player, request.ClientRequestId), Kind = "GuildGovernance", IntentDigest = SHA256.HashData(request.ToByteArray()),
                Receipt = JsonSerializer.SerializeToUtf8Bytes(new GuildGovernanceReceipt(request.ClientRequestId, receipt.ToByteArray())), TransitionId = transition! };
            db.Add(operation);
            await domain!.CompleteAsync(GuildAuthorityEvidence.Binding(operation), ct);
        }

        public async ValueTask DisposeAsync()
        {
            try { if (ordinary is not null) await ordinary.DisposeAsync(); else await domain!.DisposeAsync(); }
            finally { live?.Dispose(); if (lease is not null) await lease.DisposeAsync(); storage?.Dispose(); }
        }
    }
}
