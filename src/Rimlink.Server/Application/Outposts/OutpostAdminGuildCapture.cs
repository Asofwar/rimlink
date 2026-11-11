using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal sealed record GuildOperatorOutpost(string Id, int Tile, GuildResource Resource, long? AllianceId,
    long Revision, int CommittedStock, string Phase);
internal sealed record GuildOperatorCampaign(string Id, string OutpostId, DateTime ScheduledAtUtc,
    long AttackerAllianceId, long DefenderAllianceId, long AttackerCommanderId, long? DefenderCommanderId, string Phase);
internal sealed record GuildOperatorBattle(string Id, string CampaignId, string ExecutorId, long Attempt, long Fence, string Phase, string Boundary);
internal sealed record GuildOperatorInspection(OutpostAuthorityObservationKind Authority, GuildMutationScope? Scope,
    string? HeadId, OutpostRuntimeMode Mode, bool TransportAdmitted, bool ProductEnabled, string? AvailableExecutorId,
    GuildOperatorOutpost[] Outposts, GuildOperatorCampaign[] Campaigns, GuildOperatorBattle[] Battles, bool HasMore,
    bool Frozen, string? FreezeTransitionId);

public sealed partial class OutpostAdminService
{
    internal Task<GuildOperatorInspection> InspectGuildCaptureAsync(Actor actor, BattleExecutorRegistry executors,
        int limit, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        return authority.ReadAdminProjectionAsync<GuildOperatorInspection>(observation, live, async (observed, token) =>
        {
            var mode = authority.ReadinessObservation.Mode;
            if (observed.Committed is not { DomainFormat: OutpostDomainFormat.V3 } current)
                return new(observed.Kind, null, null, mode, false, false, null, [], [], [], false, false, null);

            bool admitted = false;
            if (authority.IsRuntimeComposition)
            {
                try { authority.RequireRuntimeGuild(); admitted = true; }
                catch (OutpostEvidenceUnavailableException) { }
            }

            var outposts = await db.Set<GuildOutpostRow>().AsNoTracking().Where(o => o.WorldId == current.World)
                .OrderBy(o => o.Tile).ThenBy(o => o.Id).Take(limit + 1)
                .Select(o => new GuildOperatorOutpost(o.Id, o.Tile, o.Resource, o.AllianceId, o.Revision, o.Stock, o.Phase))
                .ToArrayAsync(token);
            var campaigns = await db.Set<GuildCampaign>().AsNoTracking()
                .Where(c => c.WorldId == current.World && c.Boundary == current.Boundary && c.Active)
                .OrderBy(c => c.ScheduledAtUtc).ThenBy(c => c.Id).Take(limit + 1)
                .Select(c => new GuildOperatorCampaign(c.Id, c.OutpostId, c.ScheduledAtUtc, c.AttackerAllianceId,
                    c.DefenderAllianceId, c.AttackerCommanderId, c.DefenderCommanderId, c.Phase))
                .ToArrayAsync(token);
            var battles = await db.Set<GuildBattle>().AsNoTracking()
                .Where(b => b.WorldId == current.World && b.Active &&
                    (b.Boundary == current.Boundary || b.Phase == "AppliedAwaitingCleanup" || b.Phase == "CancelledAwaitingCleanup"))
                .OrderBy(b => b.Id).Take(limit + 1)
                .Select(b => new GuildOperatorBattle(b.Id, b.CampaignId, b.ExecutorId, b.Attempt, b.Fence, b.Phase, b.Boundary))
                .ToArrayAsync(token);

            var freeze = await GuildAuthorityEvidence.LatestFreezeAsync(db, current.World, token);
            return new(observed.Kind, new(current.World, current.Boundary), current.HeadId, mode,
                admitted, admitted && mode == OutpostRuntimeMode.Product, admitted ? executors.Available()?.ExecutorId : null,
                outposts.Take(limit).ToArray(), campaigns.Take(limit).ToArray(), battles.Take(limit).ToArray(),
                outposts.Length > limit || campaigns.Length > limit || battles.Length > limit,
                freeze?.Command.Frozen == true, freeze?.TransitionId);
        }, ct);
    }
}
