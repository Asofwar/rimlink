using System.Text.Json;
using Rimlink.Protocol.V1;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildCampaignRequest(GuildMutationScope Scope, string RequestId, long AllianceId, string OutpostId, long OwnerRevision, long CommanderId);
internal sealed record GuildCampaignScheduleReceipt(int Version, GuildCampaignRequest Request, string CampaignId, string SlotId,
    string ReservationId, long DefenderAllianceId, DateTime DeclaredAtUtc, DateTime ScheduledAtUtc, int DefenceHourUtc,
    string ExecutorId, string ProfileSha256, string RulesSha256, string TransitionId);

internal sealed partial class GuildOutpostService
{
    internal Task<GuildCampaignScheduleReceipt> DeclareCampaignAsync(long leader, GuildCampaignRequest request, CancellationToken ct)
    {
        if (leader <= 0 || request?.Scope is null || request.AllianceId <= 0 || request.CommanderId <= 0 || request.OwnerRevision <= 0
            || !Guid.TryParseExact(request.OutpostId, "D", out _)) throw Refused();
        return MutateAsync(request.Scope, leader, "player:" + leader, request.RequestId, "GuildCampaignScheduled", request,
            async (lease, id, token) =>
            {
                await RequireCaptureOpenHeldAsync(request.Scope.WorldId, token);
                var now = clock.Sample().Utc;
                await policy.RequireLeaderHeldAsync(lease, leader, request.AllianceId, now, token);
                await policy.RequireCommanderHeldAsync(lease, request.CommanderId, request.AllianceId, now, BattleFighterSide.Attacker, token);
                var row = await db.Set<GuildOutpostRow>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == request.OutpostId
                    && o.WorldId == request.Scope.WorldId, token) ?? throw Refused();
                if (row.AllianceId is not { } defender || defender == request.AllianceId || row.Revision != request.OwnerRevision || row.Phase != "Active"
                    || await db.Set<GuildCampaign>().AnyAsync(c => c.Active && (c.OutpostId == row.Id || c.AttackerAllianceId == request.AllianceId), token)
                    || await db.Set<GuildBattle>().AnyAsync(b => b.Active, token)) throw Refused();
                if(await GuildAuthorityEvidence.PairCoolingDownAsync(db,row.WorldId,request.AllianceId,defender,now,token))throw Refused();
                int owned = await db.Set<GuildOutpostRow>().CountAsync(o => o.WorldId == row.WorldId && o.AllianceId == request.AllianceId, token);
                int reserved = await db.Set<GuildAcquisitionReservation>().CountAsync(r => r.AllianceId == request.AllianceId && r.Active, token);
                int obligations = await db.Set<GuildResourceObligation>().CountAsync(o => o.AllianceId == defender && o.RemainingCount > 0, token);
                int futureObligations = await db.Set<GuildCampaign>().CountAsync(c => c.DefenderAllianceId == defender && c.Active, token);
                if (owned + reserved >= 3 || obligations + futureObligations >= 256) throw Refused();
                var executor = executors.Available() ?? throw Refused();
                executors.RequireExecution(executor);
                await GuildAuthorityEvidence.ProfileAdmissionAsync(db, request.Scope.WorldId, executor.ExecutorId, executor.ProfileDigest.ToByteArray(), token);
                var slots = await db.Set<GuildBattleSlot>().AsNoTracking().Where(s => s.Active).ToArrayAsync(token);
                var candidates = new List<(DateTime At, int Hour)>();
                foreach (int hour in new[] { row.DefenceHourUtc, row.PendingDefenceHourUtc ?? row.DefenceHourUtc }.Distinct())
                    for (int day = -1; day <= 8; day++)
                        for (int halfHour = 0; halfHour < 4; halfHour++)
                        {
                            DateTime at = now.Date.AddDays(day).AddHours(hour).AddMinutes(halfHour * 30);
                            int effectiveHour = row.WindowChangeAtUtc is { } change && at >= change ? row.PendingDefenceHourUtc ?? row.DefenceHourUtc : row.DefenceHourUtc;
                            if (hour != effectiveHour || slots.Any(s => s.StartsAtUtc < at + GuildBattleSchedule.ReservationDuration && s.EndsAtUtc > at)) continue;
                            if (GuildBattleSchedule.NextEligibleSlot(now, hour, row.ProtectedUntilUtc, [at]) is not null) candidates.Add((at, hour));
                        }
                if (candidates.Count == 0) throw Refused();
                var selected = candidates.OrderBy(s => s.At).First();
                string campaignId = Guid.NewGuid().ToString("D"), slotId = Guid.NewGuid().ToString("D"), reservationId = Guid.NewGuid().ToString("D");
                string profile = Convert.ToHexStringLower(executor.ProfileDigest.Span);
                var receipt = new GuildCampaignScheduleReceipt(1, request, campaignId, slotId, reservationId, defender,
                    now, selected.At, selected.Hour, executor.ExecutorId, profile, "", id);
                receipt = receipt with { RulesSha256 = Convert.ToHexStringLower(GuildAuthorityEvidence.CampaignRulesDigest(receipt)) };
                db.Add(new GuildCampaign { Id = campaignId, OutpostId = row.Id, WorldId = row.WorldId, Boundary = request.Scope.Boundary,
                    OwnerRevision = row.Revision, AttackerAllianceId = request.AllianceId, DefenderAllianceId = defender, AttackerCommanderId = request.CommanderId,
                    DeclaredAtUtc = now, ScheduledAtUtc = selected.At, RulesDigest = Convert.FromHexString(receipt.RulesSha256), Active = true });
                db.Add(new GuildAcquisitionReservation { Id = reservationId, CampaignId = campaignId, AllianceId = request.AllianceId, Active = true });
                db.Add(new GuildBattleSlot { Id = slotId, CampaignId = campaignId, ExecutorId = executor.ExecutorId,
                    StartsAtUtc = selected.At, EndsAtUtc = selected.At + GuildBattleSchedule.ReservationDuration, Active = true });
                return receipt;
            }, ct, [request.CommanderId]);
    }
}

internal static partial class GuildAuthorityEvidence
{
    internal static async Task<GuildCampaignScheduleReceipt> CampaignScheduleAsync(RimlinkDbContext db, string campaignId, CancellationToken ct)
    {
        var records = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildCampaignScheduled").Select(o => o.Receipt).ToArrayAsync(ct);
        var matches = records.Select(r => JsonSerializer.Deserialize<GuildCampaignScheduleReceipt>(r, Json)).Where(r => r?.CampaignId == campaignId).ToArray();
        return matches.Length == 1 ? matches[0]! : throw new OutpostEvidenceUnavailableException("guild-campaign-schedule-evidence-missing");
    }
    internal static byte[] CampaignRulesDigest(GuildCampaignScheduleReceipt receipt) => IntentDigest(new
    {
        Format = "Rimlink.GuildCampaignRules/v1", receipt.Request, receipt.DefenderAllianceId, receipt.ScheduledAtUtc, receipt.DefenceHourUtc,
        receipt.ExecutorId, receipt.ProfileSha256, ReadinessMinutes = 5, BattleWallMinutes = 20, CleanupMinutes = 5,
        CaptureTicks = 3000, MaximumTicks = 30000, RosterPerSide = 4, ShieldHours = 72, OldStockRetained = true,
    });

    private static async Task ValidateCampaignSchedulesAsync(RimlinkDbContext db, GuildCaptureOperation[] operations,
        IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        foreach (var operation in operations.Where(o => o.Kind == "GuildCampaignScheduled"))
        {
            GuildCampaignScheduleReceipt? receipt;
            try { receipt = JsonSerializer.Deserialize<GuildCampaignScheduleReceipt>(operation.Receipt, Json); }
            catch (JsonException) { throw Invalid(); }
            if (receipt is null || receipt.Version != 1 || receipt.Request?.Scope is null || receipt.TransitionId != operation.TransitionId
                || receipt.Request.RequestId != operation.RequestId || receipt.Request.AllianceId <= 0 || receipt.Request.CommanderId <= 0
                || receipt.Request.OwnerRevision <= 0 || receipt.DefenderAllianceId <= 0 || receipt.DefenderAllianceId == receipt.Request.AllianceId
                || receipt.DeclaredAtUtc.Kind != DateTimeKind.Utc || receipt.ScheduledAtUtc.Kind != DateTimeKind.Utc
                || receipt.ScheduledAtUtc < receipt.DeclaredAtUtc.AddHours(24) || receipt.ScheduledAtUtc > receipt.DeclaredAtUtc.AddDays(7)
                || !Hex(receipt.ProfileSha256) || !Hex(receipt.RulesSha256)
                || !CampaignRulesDigest(receipt).AsSpan().SequenceEqual(Convert.FromHexString(receipt.RulesSha256))
                || !IntentDigest(receipt.Request).AsSpan().SequenceEqual(operation.IntentDigest)
                || !transitions.TryGetValue(operation.TransitionId, out var transition) || transition.WorldId != receipt.Request.Scope.WorldId
                || transition.RestoreBoundary != receipt.Request.Scope.Boundary
                || GuildBattleSchedule.NextEligibleSlot(receipt.DeclaredAtUtc, receipt.DefenceHourUtc, receipt.DeclaredAtUtc, [receipt.ScheduledAtUtc]) is null) throw Invalid();
            var row = await db.Set<GuildCampaign>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == receipt.CampaignId, ct);
            var reservation = await db.Set<GuildAcquisitionReservation>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == receipt.ReservationId, ct);
            var slot = await db.Set<GuildBattleSlot>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == receipt.SlotId, ct);
            if (row is null || reservation is null || slot is null || row.WorldId != receipt.Request.Scope.WorldId
                || row.OutpostId != receipt.Request.OutpostId || row.OwnerRevision != receipt.Request.OwnerRevision
                || row.AttackerAllianceId != receipt.Request.AllianceId || row.DefenderAllianceId != receipt.DefenderAllianceId
                || row.AttackerCommanderId != receipt.Request.CommanderId || row.DeclaredAtUtc != receipt.DeclaredAtUtc || row.ScheduledAtUtc != receipt.ScheduledAtUtc
                || !row.RulesDigest.AsSpan().SequenceEqual(Convert.FromHexString(receipt.RulesSha256))
                || reservation.CampaignId != row.Id || reservation.AllianceId != row.AttackerAllianceId || reservation.Active != row.Active
                || slot.CampaignId != row.Id || slot.ExecutorId != receipt.ExecutorId || slot.StartsAtUtc != row.ScheduledAtUtc
                || slot.EndsAtUtc != row.ScheduledAtUtc + GuildBattleSchedule.ReservationDuration || slot.Active != row.Active) throw Invalid();
        }
        static bool Hex(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        static OutpostEvidenceUnavailableException Invalid() => new("guild-campaign-schedule-evidence-mismatch");
    }
}
