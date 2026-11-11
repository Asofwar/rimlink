using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildStartedAbortCommand(GuildMutationScope Scope, string RequestId, string ExpectedHeadId, string BattleId, string Reason);
internal sealed record GuildStartedAbortReceipt(int Version, Actor Actor, GuildStartedAbortCommand Command, GuildCampaignTerminalReceipt Terminal,
    string StartDigest, string PreparationTransitionId, string TransitionId)
{
    internal const string Kind = "GuildStartedBattleAborted";
    internal const string Outcome = "OperatorAborted";
}

internal sealed partial class GuildOutpostService
{
    internal Task<GuildStartedAbortReceipt> AbortStartedBattleAsync(Actor actor, GuildStartedAbortCommand command, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        if (command.Scope is null || !Guid.TryParseExact(command.ExpectedHeadId, "D", out _)
            || !Guid.TryParseExact(command.BattleId, "D", out _) || !ValidFreezeReason(command.Reason)) throw Refused();
        var scope = command.Scope;
        GuildRuntimeAssignment? assignment = null;
        return MutateAsync(scope, actor.PlayerId, actor.Name, command.RequestId, GuildStartedAbortReceipt.Kind,
            new { actor, command }, async (_, id, token) =>
            {
                if (!await db.OutpostAuthority.AsNoTracking().AnyAsync(a => a.HeadId == command.ExpectedHeadId, token))
                    throw new OutpostEvidenceUnavailableException("guild-abort-preview-stale");
                var battle = await db.Set<GuildBattle>().SingleOrDefaultAsync(b => b.Id == command.BattleId, token) ?? throw Refused();
                if (!battle.Active || battle.WorldId != scope.WorldId || battle.Boundary != scope.Boundary
                    || battle.Phase is not ("StartCommitted" or "AwaitingResult") || battle.StartDigest is not { Length: 32 }
                    || battle.StartManifest is null || battle.FinalDigest is not null) throw Refused();
                var campaign = await db.Set<GuildCampaign>().SingleAsync(c => c.Id == battle.CampaignId, token);
                var row = await db.Set<GuildOutpostRow>().SingleAsync(o => o.Id == campaign.OutpostId, token);
                var prepared = await GuildAuthorityEvidence.PreparationAsync(db, battle.Id, token);
                assignment = prepared.RuntimeAssignment;
                var scheduled = await GuildAuthorityEvidence.CampaignScheduleAsync(db, campaign.Id, token);
                var reservation = await db.Set<GuildAcquisitionReservation>().SingleAsync(r => r.Id == scheduled.ReservationId, token);
                var slot = await db.Set<GuildBattleSlot>().SingleAsync(s => s.Id == scheduled.SlotId, token);
                if (!campaign.Active || campaign.Phase is not ("StartCommitted" or "AwaitingResult") || !reservation.Active || !slot.Active
                    || row.WorldId != scope.WorldId || row.Phase != "Battle" || row.AllianceId != campaign.DefenderAllianceId
                    || row.Revision != campaign.OwnerRevision || row.Stock != prepared.StockAtCut || row.Remainder != prepared.RemainderAtCut) throw Refused();
                var sample = clock.Sample();
                var checkpoint = OutpostClock.Advance(new(row.CheckpointUtc, row.ClockProcessId, row.ClockTimestamp, row.ClockFrequency), sample);
                Checkpoint(row, new(checkpoint.Utc, checkpoint.ProcessId, checkpoint.Timestamp, checkpoint.Frequency));
                row.ConsecutiveAborts = checked(row.ConsecutiveAborts + 1);
                row.Phase = row.ConsecutiveAborts >= 3 ? "Maintenance" : "Active";
                campaign.Active = false;
                campaign.Phase = "TechnicalAbort";
                campaign.TerminalReason = GuildStartedAbortReceipt.Outcome;
                reservation.Active = false;
                slot.Active = false;
                battle.Phase = "CancelledAwaitingCleanup";
                foreach (var grant in await db.Set<GuildBattleControlGrant>().Where(g => g.BattleId == battle.Id && g.Active).ToArrayAsync(token))
                    grant.Active = false;
                var terminal = new GuildCampaignTerminalReceipt(1, scope, campaign.Id, row.Id, row.Revision, campaign.AttackerAllianceId,
                    campaign.DefenderAllianceId, GuildStartedAbortReceipt.Outcome, sample.Utc, battle.Id, null, reservation.Id, slot.Id, null);
                return new GuildStartedAbortReceipt(1, actor, command, terminal, Convert.ToHexStringLower(battle.StartDigest), prepared.TransitionId, id);
            }, ct, battleLock: true, afterCommit: _ =>
            {
                if (assignment is null) return;
                try { executors.HoldCleanup(executors.RequireAssignedExecution(assignment, allowStartReplay: true)); }
                catch (InvalidDataException) { }
            });
    }
}

internal static partial class GuildAuthorityEvidence
{
    private static GuildStartedAbortReceipt ReadStartedAbort(byte[] bytes) =>
        JsonSerializer.Deserialize<GuildStartedAbortReceipt>(bytes, Json) ?? throw InvalidStartedAbort();

    internal static async Task<GuildStartedAbortReceipt> StartedAbortAsync(RimlinkDbContext db, string battleId, CancellationToken ct)
    {
        var receipts = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == GuildStartedAbortReceipt.Kind)
            .Select(o => o.Receipt).ToArrayAsync(ct);
        return receipts.Select(ReadStartedAbort).SingleOrDefault(r => r.Command.BattleId == battleId) ?? throw InvalidStartedAbort();
    }

    private static async Task ValidateStartedAbortsAsync(RimlinkDbContext db, GuildCaptureOperation[] operations,
        IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        foreach (var operation in operations.Where(o => o.Kind == GuildStartedAbortReceipt.Kind))
        {
            try
            {
                var receipt = ReadStartedAbort(operation.Receipt);
                var command = receipt.Command;
                var actor = receipt.Actor;
                var terminal = receipt.Terminal;
                var transition = transitions[operation.TransitionId];
                var battle = await db.Set<GuildBattle>().AsNoTracking().SingleAsync(b => b.Id == command.BattleId, ct);
                var campaign = await db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c => c.Id == battle.CampaignId, ct);
                var schedule = await CampaignScheduleAsync(db, campaign.Id, ct);
                var preparation = await PreparationAsync(db, battle.Id, ct);
                var startReceipt = operations.Where(o => o.Kind == "GuildBattleStarted")
                    .Select(o => JsonSerializer.Deserialize<GuildBattleStartReceipt>(o.Receipt, Json) ?? throw InvalidStartedAbort())
                    .Single(r => ReadStart(r.Intent).Scope.BattleId == battle.Id);
                if (receipt.Version != 1 || terminal.Version != 1 || receipt.TransitionId != transition.Id || actor.Role != Role.Admin
                    || actor.Name != transition.Actor || actor.PlayerId != operation.ActorId || command.RequestId != operation.RequestId
                    || command.ExpectedHeadId != transition.OldHeadId || !GuildOutpostService.ValidFreezeReason(command.Reason)
                    || !IntentDigest(new { actor, command }).AsSpan().SequenceEqual(operation.IntentDigest)
                    || command.Scope != terminal.Scope || command.Scope.WorldId != transition.WorldId || command.Scope.Boundary != transition.RestoreBoundary
                    || battle.WorldId != command.Scope.WorldId || battle.Boundary != command.Scope.Boundary || battle.StartDigest is not { Length: 32 }
                    || receipt.StartDigest != Convert.ToHexStringLower(battle.StartDigest) || receipt.PreparationTransitionId != preparation.TransitionId
                    || battle.FinalDigest is not null || battle.FinalManifest is not null || battle.Phase != (battle.Active ? "CancelledAwaitingCleanup" : "Cleaned")
                    || campaign.Active || campaign.Phase != "TechnicalAbort" || campaign.TerminalReason != GuildStartedAbortReceipt.Outcome
                    || terminal.Reason != GuildStartedAbortReceipt.Outcome || terminal.BattleId != battle.Id || terminal.CampaignId != campaign.Id
                    || terminal.OutpostId != campaign.OutpostId || terminal.OwnerRevision != campaign.OwnerRevision || terminal.AttackerAllianceId != campaign.AttackerAllianceId
                    || terminal.DefenderAllianceId != campaign.DefenderAllianceId || terminal.TerminalAtUtc.Kind != DateTimeKind.Utc
                    || terminal.TerminalAtUtc < startReceipt.StartedAtUtc || terminal.ResultDigest is not null || terminal.PairCooldownUntilUtc is not null
                    || terminal.ReservationId != schedule.ReservationId || terminal.SlotId != schedule.SlotId) throw InvalidStartedAbort();
                RequireEarlier(transitions, startReceipt.TransitionId, receipt.TransitionId);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or InvalidDataException or NullReferenceException)
            { throw InvalidStartedAbort(); }
        }
    }

    private static OutpostEvidenceUnavailableException InvalidStartedAbort() => new("guild-started-abort-evidence-invalid");
}
