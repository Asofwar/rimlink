using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal static partial class GuildAuthorityEvidence
{
    private static async Task ValidateApplicationsAsync(RimlinkDbContext db,BattleArtifactStore artifacts,GuildCaptureOperation[] operations,
        IReadOnlyDictionary<string,OutpostTransition> transitions,CancellationToken ct)
    {
        foreach (var operation in operations.Where(o => o.Kind == "GuildBattleApplied"))
        {
            try
            {
                var applied = JsonSerializer.Deserialize<GuildBattleApplyReceipt>(operation.Receipt,Json) ?? throw InvalidApplication();
                var receipt = BattleAppliedReceipt.Parser.ParseFrom(applied.Receipt);
                if (applied.Version != 1 || applied.Request?.Scope is null || operation.ActorId != 0 || operation.RequestId != applied.Request.RequestId
                    || operation.TransitionId != applied.TransitionId || !IntentDigest(applied.Request).AsSpan().SequenceEqual(operation.IntentDigest)
                    || !transitions.TryGetValue(applied.TransitionId,out var transition) || transition.WorldId != applied.Request.Scope.WorldId
                    || transition.RestoreBoundary != applied.Request.Scope.Boundary || receipt.Version != 1 || receipt.TransitionId != applied.TransitionId
                    || receipt.ClientRequestId != operation.RequestId || receipt.BattleId != applied.Request.BattleId || receipt.WorldInstanceId != transition.WorldId
                    || receipt.RestoreBoundary != transition.RestoreBoundary) throw InvalidApplication();
                var battle = await db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == receipt.BattleId,ct);
                if (battle?.StartManifest is null || battle.FinalManifest is null || battle.FinalDigest is null || battle.Receipt is null || battle.View is null
                    || !battle.Receipt.AsSpan().SequenceEqual(applied.Receipt) || battle.Attempt != receipt.Attempt || battle.Fence < receipt.Fence
                    || (battle.Active ? battle.Phase != "AppliedAwaitingCleanup" : battle.Phase != "Cleaned")) throw InvalidApplication();
                var start = BattleManifestContracts.ReadPrepared(battle!.StartManifest!);
                var committed = BattleManifestContracts.ReadCommittedFinal(battle.FinalManifest!,start);
                var envelope = BattleManifestContracts.ReadFinalEnvelope(committed.ExecutorEnvelope.Span);
                var final = BattleManifestContracts.ReadFinal(envelope.Manifest.Span,start);
                byte[] digest = BattleManifestContracts.Digest(BattleDocumentKind.CommittedFinal,battle.FinalManifest!);
                if (!digest.AsSpan().SequenceEqual(battle.FinalDigest) || !receipt.CommittedFinalDigest.Span.SequenceEqual(digest)
                    || envelope.ClientRequestId != receipt.ClientRequestId || receipt.Attempt != final.Attempt || receipt.Fence != final.Fence
                    || receipt.CampaignId != final.CampaignId || receipt.OutpostId != start.OutpostId || receipt.Outcome != final.Outcome || receipt.Reason != final.Reason
                    || receipt.PreviousAllianceId != start.DefenderAllianceId || receipt.PreviousOwnerRevision != start.OwnerRevision
                    || receipt.AcquisitionReservationId != start.AcquisitionReservationId || receipt.SlotId != start.SlotId
                    || receipt.TerminalUtcTicks <= 0 || receipt.TerminalUtcTicks > DateTime.MaxValue.Ticks || receipt.ProtectedUntilUtcTicks < 0
                    || receipt.ProtectedUntilUtcTicks > DateTime.MaxValue.Ticks || receipt.ConsecutiveAborts < 0) throw InvalidApplication();
                RequireEarlier(transitions,await StartTransitionAsync(db,battle.Id,ct),applied.TransitionId);
                bool capture = final.Outcome == BattleTerminalOutcome.AttackerWin,technical = final.Outcome == BattleTerminalOutcome.TechnicalAbort;
                if (receipt.CurrentAllianceId != (capture ? start.AttackerAllianceId : start.DefenderAllianceId)
                    || receipt.CurrentOwnerRevision != start.OwnerRevision + (capture ? 1 : 0) || receipt.CurrentStock != (capture ? 0 : start.StockAtCut)
                    || receipt.CurrentRemainder != (capture ? 0 : start.RemainderAtCut) || receipt.OldStockObligationCount != (capture ? start.StockAtCut : 0)
                    || technical && (receipt.ConsecutiveAborts < 1 || receipt.Maintenance != (receipt.ConsecutiveAborts >= 3))
                    || !technical && (receipt.ConsecutiveAborts != 0 || receipt.Maintenance || receipt.ProtectedUntilUtcTicks - receipt.TerminalUtcTicks != 72 * TimeSpan.TicksPerHour)) throw InvalidApplication();
                var terminal = applied.Terminal;
                if (terminal.Version != 1 || terminal.Scope != applied.Request.Scope || terminal.CampaignId != receipt.CampaignId || terminal.OutpostId != receipt.OutpostId
                    || terminal.OwnerRevision != start.OwnerRevision || terminal.AttackerAllianceId != start.AttackerAllianceId || terminal.DefenderAllianceId != start.DefenderAllianceId
                    || terminal.Reason != final.Reason.ToString() || terminal.TerminalAtUtc.Kind != DateTimeKind.Utc || terminal.TerminalAtUtc.Ticks != receipt.TerminalUtcTicks
                    || terminal.BattleId != battle.Id || terminal.ResultDigest != Convert.ToHexStringLower(digest) || terminal.ReservationId != start.AcquisitionReservationId
                    || terminal.SlotId != start.SlotId || terminal.PairCooldownUntilUtc is not null) throw InvalidApplication();
                var campaign = await db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c => c.Id == receipt.CampaignId,ct);
                var reservation = await db.Set<GuildAcquisitionReservation>().AsNoTracking().SingleAsync(r => r.Id == receipt.AcquisitionReservationId,ct);
                var slot = await db.Set<GuildBattleSlot>().AsNoTracking().SingleAsync(s => s.Id == receipt.SlotId,ct);
                if (campaign.Active || campaign.Phase != (technical ? "TechnicalAbort" : "Completed") || campaign.TerminalReason != final.Reason.ToString()
                    || reservation.Active || slot.Active || reservation.CampaignId != campaign.Id || slot.CampaignId != campaign.Id) throw InvalidApplication();
                if (receipt.OldStockObligationCount == 0)
                {
                    if (receipt.OldStockObligationId.Length != 0) throw InvalidApplication();
                }
                else
                {
                    var obligation = await db.Set<GuildResourceObligation>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == receipt.OldStockObligationId,ct);
                    if (obligation is null || obligation.OutpostId != receipt.OutpostId || obligation.CampaignId != receipt.CampaignId || obligation.TransitionId != applied.TransitionId
                        || obligation.OwnerRevision != start.OwnerRevision || obligation.AllianceId != start.DefenderAllianceId || obligation.OriginalCount != start.StockAtCut
                        || obligation.RemainingCount < 0 || obligation.RemainingCount > obligation.OriginalCount || obligation.CreatedAtUtc.Ticks != receipt.TerminalUtcTicks) throw InvalidApplication();
                }
                var row = await db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync(o => o.Id == receipt.OutpostId,ct);
                if (row.Revision < receipt.CurrentOwnerRevision || row.Revision == receipt.CurrentOwnerRevision && row.AllianceId != receipt.CurrentAllianceId) throw InvalidApplication();
                var result = artifacts.DescriptorHeld(battle.Id,final.Fence,"result") ?? throw InvalidApplication();
                if (applied.Request.Envelope != result || !artifacts.ReadDocumentHeld(result,16384).AsSpan().SequenceEqual(committed.ExecutorEnvelope.Span)) throw InvalidApplication();
                BattleResultStaging.ValidateArtifactsHeld(artifacts,final);
                if (battle.ViewSequence != final.TerminalViewSequence || !battle.View!.AsSpan().SequenceEqual(artifacts.ReadDocumentHeld(BattleResultStaging.Descriptor(final,"view",final.TerminalView),1048576))) throw InvalidApplication();
                var journal = BattleCommandJournal.OpenHeld(artifacts.CommandDirectoryHeld(battle.Id,final.Fence),
                    new(battle.Id,final.WorldInstanceId,final.RestoreBoundary,final.ExecutorId,final.Fence,final.StartDigest.ToByteArray()),createIfMissing:false);
                var seal = journal.ValidateSeal();
                RequireRuntimeStarted(journal,start,await StartRequestAsync(db,battle.Id,ct));
                if (committed.Transport.Commands.SizeBytes != seal.SizeBytes || !committed.Transport.Commands.Sha256.Span.SequenceEqual(seal.Digest)
                    || committed.Transport.Watermarks[0].LastSequence != journal.LastSequence(1) || committed.Transport.Watermarks[1].LastSequence != journal.LastSequence(2)) throw InvalidApplication();
                byte[] transportBytes = committed.Transport.ToByteArray();
                var transport = new BattleArtifactDescriptor(1,battle.Id,final.WorldInstanceId,final.RestoreBoundary,final.ExecutorId,final.Fence,"transport",transportBytes.Length,Convert.ToHexStringLower(SHA256.HashData(transportBytes)));
                if (!artifacts.ReadDocumentHeld(transport,16384).AsSpan().SequenceEqual(transportBytes)) throw InvalidApplication();
                foreach (var descriptor in new[] { BattleResultStaging.Descriptor(final,"final",final.FinalSnapshot),BattleResultStaging.Descriptor(final,"simulation",final.SimulationJournal),
                    BattleResultStaging.Descriptor(final,"view",final.TerminalView),transport,result })
                {
                    var proof = artifacts.ValidateCompleteHeld(descriptor);
                    var linked = await db.Set<GuildBattleArtifact>().AsNoTracking().SingleOrDefaultAsync(a => a.BattleId == battle.Id && a.Kind == descriptor.Kind,ct);
                    if (linked is null || linked.RelativePath != proof.RelativePath || linked.SizeBytes != proof.SizeBytes || Convert.ToHexStringLower(linked.Digest) != descriptor.Sha256) throw InvalidApplication();
                }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or ArgumentException or NullReferenceException or InvalidProtocolBufferException)
            { throw new OutpostEvidenceUnavailableException("guild-result-evidence-invalid: " + error.GetType().Name); }
        }
    }
    private static async Task<string> StartTransitionAsync(RimlinkDbContext db,string battleId,CancellationToken ct)
    {
        var operations = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildBattleStarted").Select(o => o.Receipt).ToArrayAsync(ct);
        var matches = operations.Select(b => JsonSerializer.Deserialize<GuildBattleStartReceipt>(b,Json) ?? throw InvalidApplication())
            .Where(r => ReadStart(r.Intent).Scope.BattleId == battleId).ToArray();
        return matches.Length == 1 ? matches[0].TransitionId : throw InvalidApplication();
    }
    private static OutpostEvidenceUnavailableException InvalidApplication() => new("guild-result-evidence-mismatch");
}
