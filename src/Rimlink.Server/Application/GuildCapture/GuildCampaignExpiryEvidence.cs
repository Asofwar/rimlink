using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal static partial class GuildAuthorityEvidence
{
    internal static GuildCampaignExpiryReceipt ReadExpiry(byte[] bytes)=>JsonSerializer.Deserialize<GuildCampaignExpiryReceipt>(bytes,Json)??throw InvalidExpiry();
    private static async Task ValidateExpiriesAsync(RimlinkDbContext db,BattleArtifactStore artifacts,GuildCaptureOperation[] operations,
        IReadOnlyDictionary<string,OutpostTransition> transitions,CancellationToken ct)
    {
        foreach(var operation in operations.Where(o=>o.Kind is "GuildCampaignExpired" or GuildOperatorCancellation.Kind))
        {
            try
            {
                var receipt=ReadExpiry(operation.Receipt);var terminal=receipt.Terminal;var request=receipt.Request;
                var campaign=await db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c=>c.Id==request.CampaignId,ct);
                var schedule=await CampaignScheduleAsync(db,campaign.Id,ct);
                bool manual = operation.Kind == GuildOperatorCancellation.Kind;
                var cancellation = request.Operator;
                if (manual)
                {
                    if (cancellation is null || !cancellation.IsValid() || receipt.Version != 2 || receipt.Decision != "AdminRequest"
                        || terminal.Reason != GuildOperatorCancellation.Outcome || terminal.TerminalAtUtc < campaign.DeclaredAtUtc
                        || !transitions.TryGetValue(operation.TransitionId, out var transition)
                        || transition.OldHeadId != cancellation.ExpectedHeadId || transition.Actor != cancellation.ActorName
                        || transition.WorldId != request.Scope.WorldId || transition.RestoreBoundary != request.Scope.Boundary)
                        throw InvalidExpiry();
                }
                else if (cancellation is not null || receipt.Version != 1 || operation.ActorId != 0
                    || terminal.TerminalAtUtc < campaign.ScheduledAtUtc.AddMinutes(5)
                    || terminal.Reason is not ("NoAttackerReady" or "TechnicalBeforeStart")) throw InvalidExpiry();
                if(terminal.Version!=1 || receipt.TransitionId!=operation.TransitionId || request.Scope!=terminal.Scope
                    || !IntentDigest(request).AsSpan().SequenceEqual(operation.IntentDigest) || terminal.CampaignId!=campaign.Id || terminal.OutpostId!=campaign.OutpostId
                    || terminal.OwnerRevision!=campaign.OwnerRevision || terminal.AttackerAllianceId!=campaign.AttackerAllianceId || terminal.DefenderAllianceId!=campaign.DefenderAllianceId
                    || terminal.TerminalAtUtc.Kind!=DateTimeKind.Utc || terminal.ResultDigest is not null
                    || terminal.ReservationId!=schedule.ReservationId || terminal.SlotId!=schedule.SlotId || campaign.Active || campaign.Phase!=terminal.Reason || campaign.TerminalReason!=terminal.Reason
                    || receipt.Stock is <0 or >1000 || receipt.Remainder is <0 or >=TimeSpan.TicksPerHour
                    || receipt.Stock==1000 && receipt.Remainder!=0 || terminal.PairCooldownUntilUtc!=(terminal.Reason=="NoAttackerReady"?terminal.TerminalAtUtc.AddHours(24):null))throw InvalidExpiry();
                RequireEarlier(transitions,schedule.TransitionId,receipt.TransitionId);
                if(terminal.BattleId is {} battleId)
                {
                    var battle=await db.Set<GuildBattle>().AsNoTracking().SingleAsync(b=>b.Id==battleId,ct);var prepared=await PreparationAsync(db,battleId,ct);
                    if(!receipt.ProductionRebased || receipt.PreparationTransitionId!=prepared.TransitionId || battle.CampaignId!=campaign.Id
                        || battle.WorldId!=request.Scope.WorldId || battle.Boundary!=request.Scope.Boundary || battle.StartManifest is not null || battle.StartDigest is not null
                        || battle.FinalManifest is not null || battle.FinalDigest is not null || battle.Phase!=(battle.Active?"CancelledAwaitingCleanup":"Cleaned")
                        || receipt.Stock!=prepared.StockAtCut || receipt.Remainder!=prepared.RemainderAtCut)throw InvalidExpiry();
                    RequireEarlier(transitions,prepared.TransitionId,receipt.TransitionId);
                    if(terminal.Reason=="NoAttackerReady")
                    {
                        if(receipt.Decision!="PlayerDidNotReady" || receipt.ProposalSequence is <1 or >8 || receipt.Offer is null || receipt.Acknowledgement is null || receipt.Health is null
                            || receipt.Health.RuntimeInstanceId!=prepared.RuntimeAssignment.RuntimeInstanceId || !Guid.TryParseExact(receipt.Health.LeaseId,"D",out _)
                            || receipt.Health.LeaseFence<prepared.ExecutorFence || receipt.Health.AgeTicks<0 || receipt.Health.AgeTicks>TimeSpan.FromSeconds(30).Ticks)throw InvalidExpiry();
                        var proposal=new BattlePreparedJournal(artifacts).ReadHeld(battle.Id,battle.Fence,receipt.ProposalSequence);
                        var manifest=BattleManifestContracts.ReadPrepared(proposal.Manifest);var ack=BattleReadyOfferJournal.ReadAck(artifacts,receipt.Acknowledgement,receipt.Offer);var offer=ack.Offer;
                        if(offer.Scope.BattleId!=battle.Id || offer.Scope.WorldInstanceId!=request.Scope.WorldId || offer.Scope.RestoreBoundary!=request.Scope.Boundary
                            || offer.Scope.Attempt!=battle.Attempt || offer.Scope.Fence!=battle.Fence || offer.ProposalSequence!=proposal.Sequence || !offer.ManifestDigest.Span.SequenceEqual(proposal.ManifestDigest)
                            || offer.PlayerId!=campaign.AttackerCommanderId || !offer.SessionDigest.Equals(manifest.Attacker.SessionDigest)
                            || offer.MembershipJoinedUtcTicks!=manifest.Attacker.MembershipJoinedUtcTicks || offer.DeadlineUtcTicks!=manifest.ReadinessDeadlineUtcTicks
                            || offer.OfferedAtUtcTicks<prepared.PreparedAtUtc.Ticks || ack.ReceivedAtUtcTicks>=terminal.TerminalAtUtc.Ticks
                            || operations.Where(o=>o.Kind=="GuildAttackerReady").Select(o=>ReadReadyReceipt(o.Receipt)).Any(r=>r.Manifest.BattleId==battle.Id && r.ManifestDigest==Convert.ToHexStringLower(proposal.ManifestDigest)))throw InvalidExpiry();
                    }
                }
                else if(receipt.ProductionRebased || receipt.PreparationTransitionId is not null || !manual && (terminal.Reason!="TechnicalBeforeStart" || receipt.Decision!="NoPreparation")
                    || await db.Set<GuildBattle>().AnyAsync(b=>b.CampaignId==campaign.Id,ct))throw InvalidExpiry();
                if(terminal.Reason!="NoAttackerReady" && (receipt.Offer is not null || receipt.Acknowledgement is not null || receipt.Health is not null || receipt.ProposalSequence!=0
                    || !manual && receipt.Decision is not("NoPreparation" or "SimulationAdmissionUnavailable" or "RuntimeUnavailable" or "ProfileUnavailable" or "ParticipationUnavailable" or "ReadyNotStarted" or "NoOffer" or "NoDeliveryAck" or "OfferEvidenceUnavailable")))throw InvalidExpiry();
            }
            catch(Exception error)when(error is JsonException or InvalidDataException or InvalidOperationException or IOException or NullReferenceException or ArgumentException or OverflowException)
            {throw new OutpostEvidenceUnavailableException("guild-campaign-expiry-evidence-invalid: "+error.GetType().Name);}
        }
    }
    internal static async Task<bool> PairCoolingDownAsync(RimlinkDbContext db,string world,long attacker,long defender,DateTime now,CancellationToken ct)
    {
        var records=await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o=>o.Kind=="GuildCampaignExpired").Select(o=>o.Receipt).ToArrayAsync(ct);
        return records.Select(ReadExpiry).Any(r=>r.Terminal.Scope.WorldId==world && r.Terminal.AttackerAllianceId==attacker
            && r.Terminal.DefenderAllianceId==defender && r.Terminal.PairCooldownUntilUtc>now);
    }
    private static OutpostEvidenceUnavailableException InvalidExpiry()=>new("guild-campaign-expiry-invalid");
}
