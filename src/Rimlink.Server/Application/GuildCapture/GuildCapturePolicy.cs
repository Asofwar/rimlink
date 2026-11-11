using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed class GuildCapturePolicy(RimlinkDbContext db, EconomyGate economy, EconomyGuard guard,
    RaidGate raids, IOptions<RaidOptions> options)
{
    internal async Task RequireCommanderHeldAsync(OutpostLease lease, long commander, long alliance, DateTime now, BattleFighterSide side, CancellationToken ct)
    {
        if (now.Kind!=DateTimeKind.Utc || side is not(BattleFighterSide.Attacker or BattleFighterSide.Defender) || !lease.CoversOwner(commander) || !await db.Set<AllianceMember>().AnyAsync(m => m.AllianceId == alliance && m.PlayerId == commander, ct)) throw Refused();
        var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == commander, ct);
        if (player is null || side==BattleFighterSide.Attacker && RaidRules.IsPeaceful(player,now) || !await db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == commander && o.Type == WorldObjectTypes.Settlement, ct)
            || !await db.SaveVersions.AnyAsync(s => s.PlayerId == commander && s.IsCurrent && s.SaveGeneration == player.SaveGeneration
                && s.SaveWriteEpoch == player.SaveWriteEpoch, ct)
            || await db.ArchivedColonies.AnyAsync(a => a.PlayerId == commander && a.SaveGeneration == player.SaveGeneration && a.State == ArchiveState.Archived, ct)) throw Refused();
        await economy.RequireNotFrozen(commander); await guard.RequireNoDebt(commander); await guard.RequireNoThreat(commander);
    }
    internal async Task RequireClaimPartiesHeldAsync(OutpostLease lease, long leader, GuildResourceClaimRequest request, CancellationToken ct)
    {
        if (!lease.CoversOwner(leader) || !lease.CoversOwner(request.RecipientId)) throw Refused();
        if (!await db.Set<AllianceGroup>().AnyAsync(g => g.Id == request.AllianceId && g.OwnerId == leader && !g.Disbanded, ct)
            || !await db.Set<AllianceMember>().AnyAsync(m => m.AllianceId == request.AllianceId && m.PlayerId == leader, ct)
            || !await db.Set<AllianceMember>().AnyAsync(m => m.AllianceId == request.AllianceId && m.PlayerId == request.RecipientId, ct)) throw ClaimConflict();
        var recipient = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.RecipientId, ct);
        if (recipient is null || recipient.SaveGeneration != request.RecipientGeneration || recipient.SaveWriteEpoch != request.RecipientEpoch
            || !await db.SaveVersions.AnyAsync(s => s.PlayerId == recipient.Id && s.IsCurrent && s.SaveGeneration == recipient.SaveGeneration
                && s.SaveWriteEpoch == recipient.SaveWriteEpoch, ct)
            || !await db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == recipient.Id && o.Type == WorldObjectTypes.Settlement, ct)
            || await db.ArchivedColonies.AnyAsync(a => a.PlayerId == recipient.Id && a.SaveGeneration == recipient.SaveGeneration && a.State == ArchiveState.Archived, ct)) throw ClaimConflict();
        economy.RequireOpen();
        foreach (long player in new[] { leader, recipient.Id }.Distinct())
        { await economy.RequireNotFrozen(player); await guard.RequireNoDebt(player); await guard.RequireNoThreat(player); }
    }

    internal async Task RequireLeaderHeldAsync(OutpostLease lease, long leader, long alliance, DateTime now, CancellationToken ct)
    {
        if (!lease.CoversOwner(leader) || now.Kind != DateTimeKind.Utc) throw Refused();
        if (!await db.Set<AllianceGroup>().AnyAsync(g => g.Id == alliance && g.OwnerId == leader && !g.Disbanded, ct)
            || !await db.Set<AllianceMember>().AnyAsync(m => m.AllianceId == alliance && m.PlayerId == leader, ct)) throw Refused();
        var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == leader, ct) ?? throw Refused();
        var first = player.FirstSaveAt ?? await db.SaveVersions.Where(s => s.PlayerId == leader).MinAsync(s => (DateTime?)s.CreatedAt, ct);
        if (RaidRules.IsPeaceful(player, now) || first is null || now - first.Value < TimeSpan.FromDays(options.Value.NewbieProtectionDays)
            || !await db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == leader && o.Type == WorldObjectTypes.Settlement, ct)
            || !await db.SaveVersions.AnyAsync(s => s.PlayerId == leader && s.IsCurrent && s.SaveGeneration == player.SaveGeneration, ct)) throw Refused();
        raids.RequireOpen(); economy.RequireOpen();
        await economy.RequireNotFrozen(leader); await guard.RequireNoDebt(leader); await guard.RequireNoThreat(leader);
    }

    internal static RpcException ClaimConflict() => RpcErrors.Failed(ErrorCode.OutpostConflict, RefusalKeys.GuildClaim,
        "Guild resource claim conflicts with the current source, stock or recipient.");

    private static OutpostEvidenceUnavailableException Refused() => new("guild-leader-participation-refused");
}
