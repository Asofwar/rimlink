using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildFinalIntent(GuildMutationScope Scope,string RequestId,string BattleId,BattleArtifactDescriptor Envelope);
internal sealed record GuildCampaignTerminalReceipt(int Version,GuildMutationScope Scope,string CampaignId,string OutpostId,long OwnerRevision,
    long AttackerAllianceId,long DefenderAllianceId,string Reason,DateTime TerminalAtUtc,string? BattleId,string? ResultDigest,
    string ReservationId,string SlotId,DateTime? PairCooldownUntilUtc);
internal sealed record GuildBattleApplyReceipt(int Version,GuildFinalIntent Request,byte[] Receipt,GuildCampaignTerminalReceipt Terminal,string TransitionId);

internal sealed partial class GuildOutpostService
{
    internal async Task<BattleFinalReply> ApplyBattleFinalAsync(BattleExecutorLease executor,BattleFinalEnvelope envelope,CancellationToken ct)
    {
        authority.RequireRuntimeGuild(GuildRuntimeUse.Simulation);
        var staged = await resultStaging.StageAsync(executor,envelope,ct);
        var final = staged.Final;
        var scope = new GuildMutationScope(final.WorldInstanceId,final.RestoreBoundary);
        var intent = new GuildFinalIntent(scope,staged.Envelope.ClientRequestId,final.BattleId,staged.Artifact);
        var applied = await MutateAsync(scope,0,"executor:" + executor.ExecutorId,intent.RequestId,"GuildBattleApplied",intent,async (lease,id,token) =>
        {
            await RequireCaptureOpenHeldAsync(scope.WorldId, token);
            var battle = await db.Set<GuildBattle>().SingleAsync(b => b.Id == final.BattleId,token);
            var campaign = await db.Set<GuildCampaign>().SingleAsync(c => c.Id == battle.CampaignId,token);
            if (!battle.Active || battle.Phase is not ("StartCommitted" or "AwaitingResult") || battle.FinalDigest is not null || battle.StartManifest is null
                || battle.StartedAtUtc is null || battle.WorldId != scope.WorldId || battle.Boundary != scope.Boundary || battle.Fence != final.Fence
                || battle.Attempt != final.Attempt || battle.ExecutorId != executor.ExecutorId || !campaign.Active || campaign.Phase is not ("StartCommitted" or "AwaitingResult")) throw Refused();
            var start = BattleManifestContracts.ReadPrepared(battle.StartManifest);
            _ = BattleManifestContracts.ReadFinal(staged.Envelope.Manifest.Span,start);
            if (!executor.ProfileDigest.Equals(start.ProfileDigest) || !artifacts.ReadDocumentHeld(staged.Artifact,16384).AsSpan().SequenceEqual(staged.Envelope.ToByteArray())) throw Refused();
            BattleResultStaging.ValidateArtifactsHeld(artifacts,final);
            var row = await db.Set<GuildOutpostRow>().SingleAsync(o => o.Id == campaign.OutpostId,token);
            var reservation = await db.Set<GuildAcquisitionReservation>().SingleAsync(r => r.Id == start.AcquisitionReservationId,token);
            var slot = await db.Set<GuildBattleSlot>().SingleAsync(s => s.Id == start.SlotId,token);
            if (row.AllianceId != campaign.DefenderAllianceId || row.Revision != campaign.OwnerRevision || row.Phase != "Battle"
                || row.Stock != start.StockAtCut || row.Remainder != start.RemainderAtCut || !reservation.Active || !slot.Active
                || reservation.CampaignId != campaign.Id || reservation.AllianceId != campaign.AttackerAllianceId || slot.CampaignId != campaign.Id
                || slot.ExecutorId != executor.ExecutorId) throw Refused();
            var journal = BattleCommandJournal.OpenHeld(artifacts.CommandDirectoryHeld(battle.Id,battle.Fence),
                new(battle.Id,battle.WorldId,battle.Boundary,battle.ExecutorId,battle.Fence,battle.StartDigest!));
            GuildAuthorityEvidence.RequireRuntimeStarted(journal,start,await GuildAuthorityEvidence.StartRequestAsync(db,battle.Id,token));
            if (journal.OutstandingDispatch() is not null || journal.CutPending(1) || journal.CutPending(2))
                throw new OutpostEvidenceUnavailableException(BattleResultStaging.DispatchOutcomeUnresolved);
            foreach (int side in new[] { 1,2 })
                foreach (var command in journal.PendingCommands(side))
                {
                    long sequence = command.Sequence;
                    var refused = new BattleCommandReceipt { Side = (BattleFighterSide)side,Sequence = sequence,
                        CommandDigest = ByteString.CopyFrom(SHA256.HashData(command.Command)),Disposition = BattleCommandDisposition.NotExecuted,Reason = BattleCommandReason.Terminal };
                    journal.Acknowledge(side,sequence,null,refused.ToByteArray());
                }
            var sealedJournal = journal.Seal();
            var transport = new BattleTransportSealV1 { Version = 1,WorldInstanceId = battle.WorldId,RestoreBoundary = battle.Boundary,BattleId = battle.Id,
                Attempt = battle.Attempt,Fence = battle.Fence,StartDigest = final.StartDigest,Commands = new() { Kind = BattleDocumentKind.TransportJournal,
                    Sha256 = ByteString.CopyFrom(sealedJournal.Digest),SizeBytes = sealedJournal.SizeBytes },TerminalView = final.TerminalView.Clone(),TerminalViewSequence = final.TerminalViewSequence };
            transport.Watermarks.Add(new BattleTransportWatermark { Side = BattleFighterSide.Attacker,LastSequence = journal.LastSequence(1) });
            transport.Watermarks.Add(new BattleTransportWatermark { Side = BattleFighterSide.Defender,LastSequence = journal.LastSequence(2) });
            byte[] transportBytes = transport.ToByteArray();
            var transportArtifact = new BattleArtifactDescriptor(1,battle.Id,battle.WorldId,battle.Boundary,battle.ExecutorId,battle.Fence,"transport",transportBytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(transportBytes)));
            artifacts.AppendHeld(transportArtifact,0,transportBytes);
            var committed = new BattleCommittedFinalV1 { Version = 1,ExecutorEnvelope = staged.Envelope.ToByteString(),Transport = transport };
            byte[] committedBytes = committed.ToByteArray(); _ = BattleManifestContracts.ReadCommittedFinal(committedBytes,start);
            byte[] digest = BattleManifestContracts.Digest(BattleDocumentKind.CommittedFinal,committedBytes);
            var sample = clock.Sample(); var checkpoint = OutpostClock.Advance(new(row.CheckpointUtc,row.ClockProcessId,row.ClockTimestamp,row.ClockFrequency),sample);
            Checkpoint(row,new(checkpoint.Utc,checkpoint.ProcessId,checkpoint.Timestamp,checkpoint.Frequency));
            long previousAlliance = row.AllianceId!.Value,previousRevision = row.Revision;
            string obligationId = ""; int obligationCount = 0;
            if (final.Outcome == BattleTerminalOutcome.AttackerWin)
            {
                if (row.Stock > 0)
                {
                    obligationId = Guid.NewGuid().ToString("D"); obligationCount = row.Stock;
                    db.Add(new GuildResourceObligation { Id = obligationId,OutpostId = row.Id,OwnerRevision = row.Revision,CampaignId = campaign.Id,TransitionId = id,
                        AllianceId = previousAlliance,Resource = row.Resource,OriginalCount = row.Stock,RemainingCount = row.Stock,CreatedAtUtc = checkpoint.Utc });
                }
                row.AllianceId = campaign.AttackerAllianceId; row.Revision = checked(row.Revision + 1); row.Stock = 0; row.Remainder = 0;
            }
            bool technical = final.Outcome == BattleTerminalOutcome.TechnicalAbort;
            row.ConsecutiveAborts = technical ? checked(row.ConsecutiveAborts + 1) : 0;
            row.Phase = technical && row.ConsecutiveAborts >= 3 ? "Maintenance" : "Active";
            if (!technical) row.ProtectedUntilUtc = checkpoint.Utc.AddHours(72);
            campaign.Active = false; campaign.Phase = technical ? "TechnicalAbort" : "Completed"; campaign.TerminalReason = final.Reason.ToString();
            reservation.Active = false; slot.Active = false;
            foreach (var grant in await db.Set<GuildBattleControlGrant>().Where(g => g.BattleId == battle.Id && g.Active).ToArrayAsync(token)) grant.Active = false;
            foreach (var descriptor in new[] { BattleResultStaging.Descriptor(final,"final",final.FinalSnapshot),BattleResultStaging.Descriptor(final,"simulation",final.SimulationJournal),
                BattleResultStaging.Descriptor(final,"view",final.TerminalView),transportArtifact,staged.Artifact })
            {
                var proof = artifacts.ValidateCompleteHeld(descriptor);
                db.Add(new GuildBattleArtifact { Id = Guid.NewGuid().ToString("D"),BattleId = battle.Id,Kind = descriptor.Kind,
                    Digest = Convert.FromHexString(descriptor.Sha256),SizeBytes = descriptor.SizeBytes,RelativePath = proof.RelativePath });
            }
            var receipt = new BattleAppliedReceipt { Version = 1,WorldInstanceId = battle.WorldId,RestoreBoundary = battle.Boundary,CampaignId = campaign.Id,BattleId = battle.Id,
                Attempt = battle.Attempt,Fence = battle.Fence,ClientRequestId = intent.RequestId,TransitionId = id,CommittedFinalDigest = ByteString.CopyFrom(digest),
                Outcome = final.Outcome,Reason = final.Reason,PreviousAllianceId = previousAlliance,CurrentAllianceId = row.AllianceId.Value,
                PreviousOwnerRevision = previousRevision,CurrentOwnerRevision = row.Revision,OldStockObligationId = obligationId,OldStockObligationCount = obligationCount,
                CurrentStock = row.Stock,CurrentRemainder = row.Remainder,TerminalUtcTicks = checkpoint.Utc.Ticks,ProtectedUntilUtcTicks = row.ProtectedUntilUtc.Ticks,
                ConsecutiveAborts = row.ConsecutiveAborts,Maintenance = row.Phase == "Maintenance",AcquisitionReservationId = reservation.Id,SlotId = slot.Id,OutpostId = row.Id };
            battle.FinalManifest = committedBytes; battle.FinalDigest = digest; battle.Receipt = receipt.ToByteArray();
            battle.View = artifacts.ReadDocumentHeld(BattleResultStaging.Descriptor(final,"view",final.TerminalView),1048576); battle.ViewSequence = final.TerminalViewSequence;
            battle.Phase = "AppliedAwaitingCleanup"; battle.Active = true;
            executors.HoldCleanup(executor);
            var terminal = new GuildCampaignTerminalReceipt(1,scope,campaign.Id,row.Id,previousRevision,campaign.AttackerAllianceId,campaign.DefenderAllianceId,final.Reason.ToString(),
                checkpoint.Utc,battle.Id,Convert.ToHexStringLower(digest),reservation.Id,slot.Id,null);
            return new GuildBattleApplyReceipt(1,intent,receipt.ToByteArray(),terminal,id);
        },ct,validateScope: _ => { executors.RequireCurrent(executor); return Task.CompletedTask; },battleLock:true);
        var transition = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == applied.TransitionId,ct) ?? throw Refused();
        return new() { Receipt = BattleAppliedReceipt.Parser.ParseFrom(applied.Receipt),AuthorityHeadId = transition.NextHeadId,AuthorityHeadDigest = ByteString.CopyFrom(transition.Digest) };
    }
}
