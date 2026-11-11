using System.Security.Cryptography;
using Rimlink.Protocol.V1;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildPreparationRequest(GuildMutationScope Scope, string CampaignId);
internal sealed record GuildBattlePreparationReceipt(int Version, GuildPreparationRequest Request, string BattleId, string ExecutorId,
    string ExecutorLeaseId, long ExecutorFence, string ProfileSha256, long BattleFence, string StartNonce, DateTime PreparedAtUtc,
    int StockAtCut, long RemainderAtCut, string RulesSha256, string TransitionId,GuildRuntimeAssignment RuntimeAssignment);

internal sealed partial class GuildOutpostService
{
    internal async Task<GuildBattlePreparationReceipt> PrepareCampaignAsync(GuildMutationScope scope, string requestId, string campaignId, CancellationToken ct)
    {
        if (scope is null || !Guid.TryParseExact(campaignId, "D", out _)) throw Refused();
        var observed = await db.Set<GuildCampaign>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == campaignId, ct) ?? throw Refused();
        var observedLeader = await db.Set<AllianceGroup>().AsNoTracking().SingleOrDefaultAsync(g => g.Id == observed.AttackerAllianceId, ct) ?? throw Refused();
        return await MutateAsync(scope, 0, "system:guild-preparation", requestId, "GuildBattlePrepared", new GuildPreparationRequest(scope, campaignId),
            async (lease, id, token) =>
            {
                await RequireCaptureOpenHeldAsync(scope.WorldId, token);
                var campaign = await db.Set<GuildCampaign>().SingleAsync(c => c.Id == campaignId, token);
                var alliance = await db.Set<AllianceGroup>().AsNoTracking().SingleAsync(g => g.Id == campaign.AttackerAllianceId, token);
                if (campaign.WorldId != scope.WorldId || campaign.Boundary != scope.Boundary || !campaign.Active || campaign.Phase != "Scheduled"
                    || !lease.CoversOwner(alliance.OwnerId) || !lease.CoversOwner(campaign.AttackerCommanderId)) throw Refused();
                var sample = clock.Sample();
                if (sample.Utc < campaign.ScheduledAtUtc || sample.Utc >= campaign.ScheduledAtUtc.AddMinutes(5)) throw Refused();
                await policy.RequireLeaderHeldAsync(lease, alliance.OwnerId, alliance.Id, sample.Utc, token);
                await policy.RequireCommanderHeldAsync(lease, campaign.AttackerCommanderId, alliance.Id, sample.Utc, BattleFighterSide.Attacker, token);
                var executor = executors.Available() ?? throw Refused();
                executors.RequireExecution(executor);var binding=executors.BindingOf(executor)??throw Refused();
                var approval=await GuildAuthorityEvidence.ProfileAdmissionAsync(db, scope.WorldId, executor.ExecutorId, executor.ProfileDigest.ToByteArray(), token);
                var profile=Rimlink.Protocol.BattleManifestContracts.ReadProfile(profiles.ReadHeld(approval.Request.Profile));
                if(binding.WorldInstanceId!=scope.WorldId || binding.RestoreBoundary!=scope.Boundary || !binding.InventorySha256.Equals(profile.Inventory.Sha256)
                    || !binding.ExecutorBuildSha256.Equals(profile.ExecutorBuildSha256))throw Refused();
                var scheduled = await GuildAuthorityEvidence.CampaignScheduleAsync(db, campaign.Id, token);
                if (scheduled.ProfileSha256 != Convert.ToHexStringLower(executor.ProfileDigest.Span)) throw Refused();
                var slot = await db.Set<GuildBattleSlot>().AsNoTracking().SingleOrDefaultAsync(s => s.CampaignId == campaignId && s.Active, token) ?? throw Refused();
                if (slot.ExecutorId != executor.ExecutorId || slot.StartsAtUtc != campaign.ScheduledAtUtc
                    || slot.EndsAtUtc != campaign.ScheduledAtUtc + GuildBattleSchedule.ReservationDuration
                    || await db.Set<GuildBattle>().AnyAsync(b => b.Active, token)) throw Refused();
                var row = await db.Set<GuildOutpostRow>().SingleAsync(o => o.Id == campaign.OutpostId, token);
                if (row.WorldId != scope.WorldId || row.AllianceId != campaign.DefenderAllianceId || row.Revision != campaign.OwnerRevision || row.Phase != "Active") throw Refused();
                var checkpoint = new ProductionCheckpoint(row.CheckpointUtc, row.ClockProcessId, row.ClockTimestamp, row.ClockFrequency);
                var settled = GuildProduction.Settle(row.Resource, new(row.Stock, row.Remainder), OutpostClock.Elapsed(checkpoint, sample));
                var next = OutpostClock.Advance(checkpoint, sample);
                row.Stock = settled.Count; row.Remainder = settled.Remainder; row.Phase = "PreparingBattle";
                Checkpoint(row, new(next.Utc, next.ProcessId, next.Timestamp, next.Frequency));
                campaign.Phase = "PreparingBattle"; campaign.ProductionCutUtc = sample.Utc;
                var battle = new GuildBattle { Id = Guid.NewGuid().ToString("D"), CampaignId = campaign.Id, WorldId = scope.WorldId, Boundary = scope.Boundary,
                    ExecutorId = executor.ExecutorId, Attempt = 1, Fence = 1, StartNonce = RandomNumberGenerator.GetBytes(32), Active = true };
                db.Add(battle);
                return new GuildBattlePreparationReceipt(1, new(scope, campaign.Id), battle.Id, executor.ExecutorId, executor.LeaseId, executor.Fence,
                    Convert.ToHexStringLower(executor.ProfileDigest.Span), battle.Fence,
                    Convert.ToHexStringLower(battle.StartNonce), sample.Utc, row.Stock, row.Remainder, Convert.ToHexStringLower(campaign.RulesDigest), id,new(1,scope,battle.Id,campaign.Id,battle.Attempt,battle.Fence,executor.ExecutorId,binding.RuntimeInstanceId,
                        executor.LeaseId,executor.Fence,Convert.ToHexStringLower(battle.StartNonce),approval.TransitionId,Convert.ToHexStringLower(executor.ProfileDigest.Span),
                        Convert.ToHexStringLower(binding.InventorySha256.Span),Convert.ToHexStringLower(binding.ExecutorBuildSha256.Span),sample.Utc,id,executors.OwnedProcessOf(executor)));
            }, ct, [observedLeader.OwnerId, observed.AttackerCommanderId],battleLock:true);
    }
}

internal static partial class GuildAuthorityEvidence
{
    internal static async Task<GuildBattlePreparationReceipt> PreparationAsync(RimlinkDbContext db, string battleId, CancellationToken ct)
    {
        var records = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildBattlePrepared").Select(o => o.Receipt).ToArrayAsync(ct);
        var matches = records.Select(r => JsonSerializer.Deserialize<GuildBattlePreparationReceipt>(r, Json)).Where(r => r?.BattleId == battleId).ToArray();
        return matches.Length == 1 ? matches[0]! : throw new OutpostEvidenceUnavailableException("guild-battle-preparation-evidence-missing");
    }
    private static async Task ValidatePreparationsAsync(RimlinkDbContext db, GuildCaptureOperation[] operations,
        IReadOnlyDictionary<string, OutpostTransition> transitions,BattleProfileArchive profiles, CancellationToken ct)
    {
        foreach (var operation in operations.Where(o => o.Kind == "GuildBattlePrepared"))
        {
            GuildBattlePreparationReceipt? receipt;
            try { receipt = JsonSerializer.Deserialize<GuildBattlePreparationReceipt>(operation.Receipt, Json); }
            catch (JsonException) { throw Invalid(); }
            if (receipt is null || receipt.Version != 1 || receipt.Request?.Scope is null || operation.ActorId != 0 || receipt.TransitionId != operation.TransitionId
                || !Guid.TryParseExact(receipt.ExecutorLeaseId, "D", out var lease) || lease == Guid.Empty || receipt.ExecutorFence <= 0 || receipt.BattleFence != 1
                || receipt.PreparedAtUtc.Kind != DateTimeKind.Utc || receipt.StockAtCut is < 0 or > 1000 || receipt.RemainderAtCut is < 0 or >= TimeSpan.TicksPerHour
                || receipt.StartNonce is not { Length: 64 } || receipt.StartNonce.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || !IntentDigest(receipt.Request).AsSpan().SequenceEqual(operation.IntentDigest)
                || !transitions.TryGetValue(operation.TransitionId, out var transition) || transition.WorldId != receipt.Request.Scope.WorldId
                || transition.RestoreBoundary != receipt.Request.Scope.Boundary) throw Invalid();
            var assignment=receipt.RuntimeAssignment??throw Invalid();
            var approvalOperation=operations.SingleOrDefault(o=>o.Kind=="GuildBattleProfileApproved" && o.TransitionId==assignment.ApprovalTransitionId)??throw Invalid();
            var approval=ReadProfileApproval(approvalOperation.Receipt);var profile=ReadProfileClosure(profiles,approval.Request).Profile;
            if(assignment.Version!=1 || assignment.Scope!=receipt.Request.Scope || assignment.BattleId!=receipt.BattleId || assignment.CampaignId!=receipt.Request.CampaignId
                || assignment.Attempt!=1 || assignment.Fence!=receipt.BattleFence || assignment.ExecutorId!=receipt.ExecutorId || assignment.OriginalLeaseId!=receipt.ExecutorLeaseId
                || assignment.OriginalLeaseFence!=receipt.ExecutorFence || !Guid.TryParseExact(assignment.RuntimeInstanceId,"D",out var runtime) || runtime==Guid.Empty
                || assignment.StartNonce!=receipt.StartNonce || assignment.ProfileDigest!=receipt.ProfileSha256 || assignment.InventorySha256!=Convert.ToHexStringLower(profile.Inventory.Sha256.Span)
                || assignment.ExecutorBuildSha256!=Convert.ToHexStringLower(profile.ExecutorBuildSha256.Span) || assignment.AssignedAtUtc!=receipt.PreparedAtUtc
                || assignment.TransitionId!=receipt.TransitionId || approval.ProfileDigest!=assignment.ProfileDigest || approval.ExecutorId!=assignment.ExecutorId
                || assignment.OwnedProcess is { Valid: false })throw Invalid();
            RequireEarlier(transitions,approval.TransitionId,receipt.TransitionId);
            var battle = await db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == receipt.BattleId, ct);
            var campaign = await db.Set<GuildCampaign>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == receipt.Request.CampaignId, ct);
            var scheduled = await CampaignScheduleAsync(db, receipt.Request.CampaignId, ct);
            if (battle is null || campaign is null || battle.CampaignId != campaign.Id || battle.WorldId != receipt.Request.Scope.WorldId
                || receipt.ProfileSha256 != scheduled.ProfileSha256 || receipt.ExecutorId != scheduled.ExecutorId
                || battle.ExecutorId != receipt.ExecutorId || battle.Fence < receipt.BattleFence || battle.Attempt != 1
                || Convert.ToHexStringLower(battle.StartNonce) != receipt.StartNonce || campaign.ProductionCutUtc != receipt.PreparedAtUtc
                || receipt.PreparedAtUtc < campaign.ScheduledAtUtc || receipt.PreparedAtUtc >= campaign.ScheduledAtUtc.AddMinutes(5)
                || Convert.ToHexStringLower(campaign.RulesDigest) != receipt.RulesSha256) throw Invalid();
        }
        static OutpostEvidenceUnavailableException Invalid() => new("guild-battle-preparation-evidence-mismatch");
    }
}
