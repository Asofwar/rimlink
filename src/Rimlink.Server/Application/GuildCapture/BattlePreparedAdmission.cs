using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattlePreparedAdmission(BattleExecutorRegistry executors,BattleTransportAdmission admission,BattleExecutorWorkService work,
    BattleArtifactStore artifacts,BattleProfileArchive profiles,SessionRegistry sessions)
{
    internal async Task<BattlePreparedProposalReply> ProposeAsync(BattlePreparedProposalRequest request,CancellationToken ct)
    {
        if (request is null || request.CalculateSize() > 16384 || request.Executor is null) throw new InvalidDataException("battle-proposal-refused");
        request = request.Clone();
        var manifest = BattleManifestContracts.ReadPrepared(request.Manifest.Span);
        var executor = new BattleExecutorLease(request.Executor.ExecutorId,request.Executor.LeaseId,request.Executor.Fence,request.Executor.ProfileDigest);
        executors.RequireCurrent(executor);
        await using var held = await admission.HoldAsync(ct);
        var (battle,campaign) = await work.RequirePreparingAsync(held,executor,manifest.BattleId,ct);
        await ValidateHeldAsync(held.Db,manifest,battle,campaign,executor,profiles,artifacts,sessions,ct);
        executors.RequireCurrent(executor);
        var proposal = new BattlePreparedJournal(artifacts).ProposeHeld(request.Manifest.Span);
        return new() { ProposalSequence = proposal.Sequence,ManifestDigest = ByteString.CopyFrom(proposal.ManifestDigest),
            Artifact = new() { Scope = new() { WorldInstanceId = battle.WorldId,RestoreBoundary = battle.Boundary,BattleId = battle.Id,
                Attempt = battle.Attempt,Fence = battle.Fence },Kind = proposal.Descriptor.Kind,SizeBytes = proposal.Descriptor.SizeBytes,
                Sha256 = ByteString.CopyFrom(Convert.FromHexString(proposal.Descriptor.Sha256)) } };
    }
    internal static async Task<ApprovedGuildBattleProfile> ValidateHeldAsync(RimlinkDbContext db,BattlePreparedManifestV1 value,GuildBattle battle,GuildCampaign campaign,
        BattleExecutorLease executor,BattleProfileArchive profiles,BattleArtifactStore artifacts,SessionRegistry sessions,CancellationToken ct,bool requireDefenderSession=true)
    {
        var prepared = await GuildAuthorityEvidence.PreparationAsync(db,battle.Id,ct);
        var schedule = await GuildAuthorityEvidence.CampaignScheduleAsync(db,campaign.Id,ct);
        var approved = await GuildAuthorityEvidence.RequireApprovedProfileAsync(db,profiles,value.ApprovalTransitionId,executor.ExecutorId,executor.ProfileDigest.ToByteArray(),ct);
        var profile = approved.Profile;
        if (approved.Approval.Request.Scope.WorldId != battle.WorldId || value.WorldInstanceId != battle.WorldId || value.RestoreBoundary != battle.Boundary
            || value.CampaignId != campaign.Id || value.Attempt != battle.Attempt || value.Fence != battle.Fence || value.OutpostId != campaign.OutpostId
            || value.OwnerRevision != campaign.OwnerRevision || value.AttackerAllianceId != campaign.AttackerAllianceId || value.DefenderAllianceId != campaign.DefenderAllianceId
            || value.ExecutorId != executor.ExecutorId || value.ExecutorLeaseId != prepared.RuntimeAssignment.OriginalLeaseId || value.ExecutorLeaseFence != prepared.RuntimeAssignment.OriginalLeaseFence
            || value.RuntimeInstanceId!=prepared.RuntimeAssignment.RuntimeInstanceId || value.ApprovalTransitionId!=prepared.RuntimeAssignment.ApprovalTransitionId
            || !value.StartNonce.Span.SequenceEqual(battle.StartNonce) || !value.ProfileDigest.Equals(executor.ProfileDigest)
            || value.ProfileId != profile.ProfileId || value.ProfileRevision != profile.Revision || !value.MeasuredInventory.Equals(profile.Inventory)
            || !value.RulesDigest.Span.SequenceEqual(campaign.RulesDigest) || !value.Template.Equals(profile.Template) || !value.RosterDigest.Equals(profile.RosterDigest)
            || !value.Arena.Equals(profile.Arena) || value.PreparationTransitionId != prepared.TransitionId || value.ProductionCutUtcTicks != prepared.PreparedAtUtc.Ticks
            || value.StockAtCut != prepared.StockAtCut || value.RemainderAtCut != prepared.RemainderAtCut
            || value.AcquisitionReservationId != schedule.ReservationId || value.SlotId != schedule.SlotId || value.SlotStartUtcTicks != campaign.ScheduledAtUtc.Ticks
            || value.SlotEndUtcTicks != (campaign.ScheduledAtUtc + GuildBattleSchedule.ReservationDuration).Ticks
            || value.ReadinessDeadlineUtcTicks != campaign.ScheduledAtUtc.AddMinutes(5).Ticks) throw Refused();
        for (int i = 0;i < 8;i++) if (value.Entities[i].FighterId != profile.Fighters[i].FighterId || value.Entities[i].Side != profile.Fighters[i].Side) throw Refused();
        var row = await db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync(o => o.Id == campaign.OutpostId,ct);
        var reservation = await db.Set<GuildAcquisitionReservation>().AsNoTracking().SingleAsync(r => r.Id == schedule.ReservationId,ct);
        var slot = await db.Set<GuildBattleSlot>().AsNoTracking().SingleAsync(s => s.Id == schedule.SlotId,ct);
        if (row.Revision != campaign.OwnerRevision || row.AllianceId != campaign.DefenderAllianceId || row.Phase != "PreparingBattle"
            || row.Stock != prepared.StockAtCut || row.Remainder != prepared.RemainderAtCut || !reservation.Active || !slot.Active
            || reservation.CampaignId != campaign.Id || slot.CampaignId != campaign.Id || slot.ExecutorId != executor.ExecutorId) throw Refused();
        _ = artifacts.ValidateCompleteHeld(new(1,battle.Id,battle.WorldId,battle.Boundary,battle.ExecutorId,battle.Fence,"initial",
            value.InitialSnapshot.SizeBytes,Convert.ToHexStringLower(value.InitialSnapshot.Sha256.Span)));
        if (!value.Defender.DefenderAi && (value.Defender.PlayerId!=campaign.DefenderCommanderId || value.Defender.AllianceId!=campaign.DefenderAllianceId
            || value.Defender.ControlEpoch!=value.ProposalSequence)) throw Refused();
        var attacker = await BattleCommanderIdentity.RequireAsync(db,sessions,value.Attacker,campaign.AttackerAllianceId,campaign.AttackerCommanderId,value.ProposalSequence,false,ct);
        var defender = requireDefenderSession ? await BattleCommanderIdentity.RequireAsync(db,sessions,value.Defender,campaign.DefenderAllianceId,campaign.DefenderCommanderId ?? 0,value.ProposalSequence,true,ct) : null;
        foreach (var binding in new[] { value.Attacker,value.Defender }.Where(b => !b.DefenderAi))
        {
            var existing = await db.Set<GuildBattleControlGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.Id == binding.ControlGrantId,ct);
            int side = binding.AllianceId == campaign.AttackerAllianceId ? 1 : 2;
            if (existing is not null && (existing.BattleId != battle.Id || existing.PlayerId != binding.PlayerId || existing.Epoch != binding.ControlEpoch || existing.Side != side)) throw Refused();
        }
        BattleCommanderIdentity.RequireStillCurrent(sessions,attacker); BattleCommanderIdentity.RequireStillCurrent(sessions,defender);
        return approved;
    }
    private static OutpostEvidenceUnavailableException Refused() => new("battle-prepared-manifest-binding-mismatch");
}
