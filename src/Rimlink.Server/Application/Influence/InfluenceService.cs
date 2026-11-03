using System.Data;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using WorldRow = Rimlink.Server.Storage.Entities.World;

namespace Rimlink.Server.Application.Influence;

public sealed class InfluenceService(RimlinkDbContext db, EventBus events, InfluenceProcessEpoch process, AdminAudit audit)
{
    private sealed record Target(IInfluenceRecord Record, long PlayerId, long SettlementId, long ArchiveId, int Tile, long Generation);

    public async Task<InfluenceSnapshot> GetSnapshot(CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var world = await CurrentWorld(ct);
        if (await db.WorldObjects.CountAsync(o => o.InfluenceEnabled, ct) > InfluenceLimits.MaxSources) throw Unavailable();
        var rows = await db.WorldObjects.Include(o => o.Owner).AsNoTracking().Where(o => o.InfluenceEnabled).OrderBy(o => o.Id).ToListAsync(ct);
        if (rows.Count > InfluenceLimits.MaxSources) throw Unavailable();
        var owners = rows.Select(o => o.OwnerPlayerId).ToArray();
        var members = await db.Set<AllianceMember>().AsNoTracking().Where(m => owners.Contains(m.PlayerId)).ToDictionaryAsync(m => m.PlayerId, ct);
        var ids = members.Values.Select(m => m.AllianceId).Distinct().ToArray();
        var groups = await db.Set<AllianceGroup>().AsNoTracking().Where(g => ids.Contains(g.Id) && !g.Disbanded).ToDictionaryAsync(g => g.Id, ct);
        var result = new InfluenceSnapshot { WorldInstanceId = world.InfluenceInstanceId, ProcessInstanceId = process.Id, Revision = world.InfluenceRevision, AlgorithmVersion = InfluenceLimits.AlgorithmVersion };
        foreach (var row in rows)
        {
            if (row.Type != WorldObjectTypes.Settlement || row.Tile < 0 || !members.TryGetValue(row.OwnerPlayerId, out var member)
                || !groups.ContainsKey(member.AllianceId) || !Eligible(row, new InfluenceContext { WorldInstanceId = world.InfluenceInstanceId, SaveGeneration = row.Owner.SaveGeneration, AllianceId = member.AllianceId }, row.Tile)) throw Unavailable();
            result.Sources.Add(new InfluenceSource { Id = row.Id, AllianceId = member.AllianceId, Tile = row.Tile, Radius = InfluenceLimits.Radius });
        }
        foreach (long id in result.Sources.Select(s => s.AllianceId).Distinct().Order())
        {
            var group = groups[id];
            int color = group.PaletteIndex ?? (int)(group.Id % InfluenceLimits.PaletteSize);
            if (color is < 0 or >= InfluenceLimits.PaletteSize || Encoding.UTF8.GetByteCount(group.Name) is < 1 or > InfluenceLimits.MaxNameBytes) throw Unavailable();
            result.Alliances.Add(new InfluenceAlliance { Id = id, Name = group.Name, PaletteIndex = color });
        }
        if (result.CalculateSize() > InfluenceLimits.MaxSnapshotBytes) throw Unavailable();
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<InfluenceSetting> GetSetting(long playerId, InfluenceTarget request, CancellationToken ct = default, Actor? administrator = null)
    {
        administrator?.Require(Storage.Entities.Role.Admin);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var target = await FindTarget(request, playerId, ct, administrator is not null);
        var context = await Context(target, await CurrentWorld(ct), ct);
        var state = new InfluenceSetting { Context = context, Version = target.Record.InfluenceVersion, Enabled = target.Record.InfluenceEnabled, Tile = target.Tile, ArchiveId = target.ArchiveId };
        if (target.Record.InfluenceRootSurface is { } root) state.RootSurface = root;
        await tx.CommitAsync(ct);
        return state;
    }

    public async Task<InfluenceReceipt> Mutate(long playerId, InfluenceMutation request, CancellationToken ct = default, Actor? administrator = null, string? receiptIdentity = null, InfluenceTarget? adminTarget = null)
    {
        Validate(request);
        administrator?.Require(Storage.Entities.Role.Admin);
        if (administrator is not null && request.ActionCase != InfluenceMutation.ActionOneofCase.Color
            && (request.ActionCase != InfluenceMutation.ActionOneofCase.Publication || request.Publication.Enabled))
            throw RpcErrors.PermissionDenied("an administrator may disable publication, never grant consent");
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        string actor = administrator is null ? "player:" + playerId : "admin:" + (receiptIdentity ?? throw new ArgumentNullException(nameof(receiptIdentity)));
        byte[] intent = request.ToByteArray();
        if (adminTarget is not null) intent = [.. Encoding.UTF8.GetBytes($"archive:{adminTarget.ArchiveId};settlement:{adminTarget.SettlementId};"), .. intent];
        var prior = await db.Set<InfluenceOperation>().AsNoTracking().SingleOrDefaultAsync(o => o.Actor == actor && o.RequestId == request.ClientRequestId, ct);
        if (prior is not null)
        {
            if (!prior.Intent.AsSpan().SequenceEqual(intent)) throw Refused(ErrorCode.InfluenceIntent, "this request id belongs to another intent");
            return InfluenceReceipt.Parser.ParseFrom(prior.Result);
        }
        var world = await CurrentWorld(ct);
        if (world.InfluenceRevision == long.MaxValue) throw Unavailable();
        var receipt = new InfluenceReceipt { PublicRevision = world.InfluenceRevision };
        AdminAction? auditRow = null;
        try
        {
            if (request.ActionCase == InfluenceMutation.ActionOneofCase.Color)
            {
                var color = request.Color;
                if (color.WorldInstanceId != world.InfluenceInstanceId) throw Conflict();
                var group = await db.Set<AllianceGroup>().SingleOrDefaultAsync(g => g.Id == color.AllianceId && !g.Disbanded, ct);
                if (group is null || administrator is null && group.OwnerId != playerId) throw RpcErrors.PermissionDenied("only the alliance owner can change its color");
                group.PaletteIndex = color.PaletteIndex;
            }
            else
            {
                var supplied = request.Publication?.Context ?? request.Surface.Context;
                var target = await FindTarget(adminTarget ?? new InfluenceTarget { SettlementId = supplied.SettlementId }, playerId, ct, administrator is not null);
                var context = await Context(target, world, ct);
                long version = request.Publication?.ExpectedVersion ?? request.Surface.ExpectedVersion;
                if (!supplied.Equals(context) || version != target.Record.InfluenceVersion) throw Conflict();
                if (target.Record.InfluenceVersion == long.MaxValue) throw Unavailable();
                if (request.ActionCase == InfluenceMutation.ActionOneofCase.Surface)
                {
                    if (target.ArchiveId != 0 || request.Surface.Tile != target.Tile || target.Tile < 0) throw Conflict();
                    var record = target.Record;
                    record.InfluenceRootSurface = request.Surface.RootSurface;
                    record.InfluenceWorldId = context.WorldInstanceId;
                    record.InfluenceSaveGeneration = context.SaveGeneration;
                    record.InfluenceAllianceId = context.AllianceId;
                    record.InfluenceTile = target.Tile;
                    if (!request.Surface.RootSurface) record.InfluenceEnabled = false;
                }
                else
                {
                    bool enabled = request.Publication!.Enabled;
                    if (enabled)
                    {
                        if (target.ArchiveId != 0 || !Eligible(target.Record, context, target.Tile)) throw Refused(ErrorCode.InfluenceIneligible, "confirm this colony's root surface and alliance before publishing");
                        if (!target.Record.InfluenceEnabled && await db.WorldObjects.CountAsync(o => o.InfluenceEnabled, ct) >= InfluenceLimits.MaxSources)
                            throw Refused(ErrorCode.InfluenceCapacity, "the world's influence source limit has been reached");
                    }
                    target.Record.InfluenceEnabled = enabled;
                }
                receipt.AppliedVersion = ++target.Record.InfluenceVersion;
            }
            receipt.PublicRevision = ++world.InfluenceRevision;
            Actor auditActor = administrator ?? await db.Players.AsNoTracking().Where(p => p.Id == playerId).Select(p => new Actor(p.AccountId, p.Id, Storage.Entities.Role.Player, p.Name)).SingleAsync(ct);
            auditRow = audit.Stage(auditActor, "influence." + request.ActionCase.ToString().ToLowerInvariant(), null, request.ClientRequestId);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            receipt.Error = ErrorDetail.Parser.ParseFrom(ex.Trailers.Single(t => t.Key == RpcErrors.DetailTrailer).ValueBytes);
        }
        db.Add(new InfluenceOperation { Actor = actor, RequestId = request.ClientRequestId, Intent = intent, Result = receipt.ToByteArray() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (receipt.Error is null)
        {
            Notify(world);
            if (auditRow is not null) await audit.Publish(auditRow);
        }
        return receipt;
    }

    private async Task<Target> FindTarget(InfluenceTarget request, long playerId, CancellationToken ct, bool administrator = false)
    {
        if ((request.SettlementId > 0) == (request.ArchiveId > 0)) throw Refused(ErrorCode.InfluenceInvalid, "provide one settlement or archive id");
        if (request.SettlementId > 0)
        {
            var home = await db.WorldObjects.Include(o => o.Owner).SingleOrDefaultAsync(o => o.Id == request.SettlementId && o.Type == WorldObjectTypes.Settlement, ct);
            if (home is not null)
            {
                if (!administrator && home.OwnerPlayerId != playerId) throw RpcErrors.PermissionDenied("only the colony owner may change publication");
                return new(home, home.OwnerPlayerId, home.Id, 0, home.Tile, home.Owner.SaveGeneration);
            }
        }
        var archive = request.ArchiveId > 0
            ? await db.ArchivedColonies.SingleOrDefaultAsync(a => a.Id == request.ArchiveId && a.State == ArchiveState.Archived, ct)
            : await db.ArchivedColonies.SingleOrDefaultAsync(a => a.InfluenceSettlementId == request.SettlementId && (administrator || a.PlayerId == playerId) && a.State == ArchiveState.Archived, ct);
        if (archive is null) throw Conflict();
        if (!administrator && archive.PlayerId != playerId) throw RpcErrors.PermissionDenied("only the colony owner may change publication");
        return new(archive, archive.PlayerId, archive.InfluenceSettlementId, archive.Id, archive.Tile, archive.SaveGeneration);
    }

    private async Task<InfluenceContext> Context(Target target, WorldRow world, CancellationToken ct) => new()
    {
        WorldInstanceId = world.InfluenceInstanceId, SettlementId = target.SettlementId, SaveGeneration = target.Generation,
        AllianceId = await db.Set<AllianceMember>().Where(m => m.PlayerId == target.PlayerId && db.Set<AllianceGroup>().Any(g => g.Id == m.AllianceId && !g.Disbanded)).Select(m => m.AllianceId).SingleOrDefaultAsync(ct),
    };

    private async Task<WorldRow> CurrentWorld(CancellationToken ct)
    {
        var world = await db.World.SingleOrDefaultAsync(ct);
        if (world is null || !Guid.TryParseExact(world.InfluenceInstanceId, "N", out _) || world.InfluenceRevision < 0) throw Unavailable();
        return world;
    }

    public void Notify(WorldRow world) => events.Broadcast(new ServerEvent { InfluenceChanged = new InfluenceChanged { WorldInstanceId = world.InfluenceInstanceId, ProcessInstanceId = process.Id, Revision = world.InfluenceRevision } });

    public static bool Eligible(IInfluenceRecord record, InfluenceContext context, int tile) =>
        context.AllianceId > 0 && record.InfluenceRootSurface == true && record.InfluenceWorldId == context.WorldInstanceId
        && record.InfluenceSaveGeneration == context.SaveGeneration && record.InfluenceAllianceId == context.AllianceId && record.InfluenceTile == tile;

    private static void Validate(InfluenceMutation request)
    {
        if (request.ClientRequestId.Length is < 1 or > 64 || request.ClientRequestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || request.CalculateSize() > InfluenceLimits.MaxMutationBytes)
            throw Refused(ErrorCode.InfluenceInvalid, "invalid influence request size or id");
        if (request.ActionCase == InfluenceMutation.ActionOneofCase.Publication && request.Publication.HasEnabled && request.Publication.Context is not null) return;
        if (request.ActionCase == InfluenceMutation.ActionOneofCase.Surface && request.Surface.HasRootSurface && request.Surface.Context is not null) return;
        if (request.ActionCase == InfluenceMutation.ActionOneofCase.Color && request.Color.PaletteIndex is >= 0 and < InfluenceLimits.PaletteSize) return;
        throw Refused(ErrorCode.InfluenceInvalid, "the requested value and context must be explicit");
    }

    private static RpcException Conflict() => Refused(ErrorCode.InfluenceConflict, "the colony, alliance or setting changed; refresh before trying again");
    private static RpcException Unavailable() => Refused(ErrorCode.InfluenceUnavailable, "the complete influence snapshot is unavailable");
    private static RpcException Refused(ErrorCode code, string text) => RpcErrors.Failed(code, InfluenceRefusalKeys.For(code), text);
}
