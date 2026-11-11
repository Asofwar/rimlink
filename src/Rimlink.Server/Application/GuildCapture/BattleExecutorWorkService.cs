using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleExecutorWorkService(BattleExecutorRegistry executors, BattleTransportAdmission admission, OutpostClock clock, BattleArtifactStore artifacts)
{
    internal async Task<BattleExecutorWorkReply> PollAsync(BattleExecutorLease executor, CancellationToken ct)
    {
        executors.RequireExecutionTransport(executor);
        await using var held = await admission.HoldAsync(ct);
        executors.RequireCurrent(executor);
        var active = await held.Db.Set<GuildBattle>().AsNoTracking().Where(b => b.Active && b.ExecutorId == executor.ExecutorId).Take(2).ToArrayAsync(ct);
        var reply = new BattleExecutorWorkReply { Lease = executors.Wire(executor) };
        if (active.Length == 0) return reply;
        if (active.Length != 1) throw Refused();
        if(active[0].Phase=="CancelledAwaitingCleanup")
        {
            reply.Cancelled = await CancellationWorkAsync(held, executors, executor, active[0], ct);
            return reply;
        }
        var (battle, campaign) = await RequirePreparingAsync(held, executor, active[0].Id, ct);
        reply.Preparing = new() { Scope = new() { WorldInstanceId = battle.WorldId, RestoreBoundary = battle.Boundary, BattleId = battle.Id,
            Attempt = battle.Attempt, Fence = battle.Fence }, CampaignId = battle.CampaignId, StartNonce = ByteString.CopyFrom(battle.StartNonce),
            RulesDigest = ByteString.CopyFrom(campaign.RulesDigest), OwnerRevision = campaign.OwnerRevision,Assignment=(await GuildAuthorityEvidence.PreparationAsync(held.Db,battle.Id,ct)).RuntimeAssignment.Message(),
            ReadinessDeadlineUnixMs = new DateTimeOffset(campaign.ScheduledAtUtc.AddMinutes(5)).ToUnixTimeMilliseconds() };
        return reply;
    }

    internal static async Task<BattleCancellationWork> CancellationWorkAsync(BattleTransportAdmission.Lease held,
        BattleExecutorRegistry executors, BattleExecutorLease executor, GuildBattle battle, CancellationToken ct)
    {
        if (battle.WorldId != held.Cut.WorldId || battle.Boundary != held.Cut.Boundary || battle.Phase != "CancelledAwaitingCleanup")
            throw Refused();
        var target = await GuildAuthorityEvidence.CleanupTargetAsync(held.Db, battle, ct);
        if (executors.RequireAssignedExecution(target.Assignment, allowStartReplay: true) != executor) throw Refused();
        return new() { Assignment = target.Assignment.Message(), Terminal = target.Terminal };
    }

    internal async Task<(GuildBattle Battle, GuildCampaign Campaign)> RequirePreparingAsync(BattleTransportAdmission.Lease held,
        BattleExecutorLease executor, string battleId, CancellationToken ct)
    {
        var battle = await held.Db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == battleId && b.Active && b.ExecutorId == executor.ExecutorId, ct) ?? throw Refused();
        var campaign = await held.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c => c.Id == battle.CampaignId, ct);
        var preparation = await GuildAuthorityEvidence.PreparationAsync(held.Db, battle.Id, ct);
        await GuildAuthorityEvidence.ProfileAdmissionAsync(held.Db, battle.WorldId, executor.ExecutorId, executor.ProfileDigest.ToByteArray(), ct);
        if (battle.WorldId != held.Cut.WorldId || battle.Boundary != held.Cut.Boundary || battle.Phase != "Prepared" || battle.StartDigest is not null
            || !campaign.Active || campaign.Phase != "PreparingBattle" || campaign.WorldId != battle.WorldId || campaign.Boundary != battle.Boundary
            || executors.RequireAssignedExecution(preparation.RuntimeAssignment)!=executor
            || preparation.ProfileSha256 != Convert.ToHexStringLower(executor.ProfileDigest.Span)
            || clock.Sample().Utc >= campaign.ScheduledAtUtc.AddMinutes(5)) throw Refused();
        executors.RequireCurrent(executor);
        return (battle, campaign);
    }
    private static OutpostEvidenceUnavailableException Refused() => new("battle-executor-work-reconciliation-required");
}
