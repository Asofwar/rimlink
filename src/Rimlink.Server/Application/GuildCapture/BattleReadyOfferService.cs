using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed class BattleReadyOfferService(BattleExecutorRegistry executors,BattleTransportAdmission admission,BattleArtifactStore artifacts,
    BattleProfileArchive profiles,SessionRegistry sessions,OutpostClock clock)
{
    internal async Task<BattleReadyOfferReply> ReadAsync(Session session,BattleReadyOfferRequest request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>1024 || !Uuid(request.ClientRequestId))throw Refused();request=request.Clone();
        await using var held=await admission.HoldAsync(ct);var(manifest,proposal)=await RequireOfferAsync(held,session,request.Scope,ct);
        int free=0;BattleReadyOfferEvidenceV1? selected=null;
        for(int i=1;i<=64;i++)
        {
            var descriptor=artifacts.DescriptorHeld(manifest.BattleId,manifest.Fence,"offer-"+i);
            if(descriptor is null){free=i;break;}
            var offer=BattleReadyOfferJournal.ReadOffer(artifacts,descriptor);
            if(offer.ClientRequestId!=request.ClientRequestId)continue;
            if(!Matches(offer,manifest,session) || !offer.ManifestDigest.Span.SequenceEqual(proposal.ManifestDigest))throw Refused();selected=offer;break;
        }
        if(selected is null)
        {
            if(free==0)throw Refused();var sample=clock.Sample();
            selected=new(){Version=1,Scope=request.Scope.Clone(),ProposalSequence=proposal.Sequence,ManifestDigest=ByteString.CopyFrom(proposal.ManifestDigest),
                DeliveryId=Guid.NewGuid().ToString("D"),Nonce=ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),ClientRequestId=request.ClientRequestId,
                PlayerId=session.PlayerId,SessionDigest=BattleCommanderIdentity.Digest(session),MembershipJoinedUtcTicks=manifest.Attacker.MembershipJoinedUtcTicks,
                OfferedAtUtcTicks=sample.Utc.Ticks,DeadlineUtcTicks=manifest.ReadinessDeadlineUtcTicks,ClockProcessId=sample.ProcessId,ClockTimestamp=sample.Timestamp,ClockFrequency=sample.Frequency};
            BattleReadyOfferJournal.Write(artifacts,manifest,"offer-"+free,selected);
        }
        return new(){Scope=selected.Scope.Clone(),Manifest=ByteString.CopyFrom(proposal.Manifest),ManifestDigest=selected.ManifestDigest,ProposalSequence=selected.ProposalSequence,
            DeliveryId=selected.DeliveryId,Nonce=selected.Nonce,ReadinessDeadlineUtcTicks=selected.DeadlineUtcTicks};
    }
    internal async Task<BattleReadyOfferAckReply> AcknowledgeAsync(Session session,BattleReadyOfferAckRequest request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>1024 || !Uuid(request.DeliveryId) || request.Nonce.Length!=32)throw Refused();request=request.Clone();
        await using var held=await admission.HoldAsync(ct);var(manifest,proposal)=await RequireOfferAsync(held,session,request.Scope,ct);
        for(int i=1;i<=64;i++)
        {
            var descriptor=artifacts.DescriptorHeld(manifest.BattleId,manifest.Fence,"offer-"+i);if(descriptor is null)break;
            var offer=BattleReadyOfferJournal.ReadOffer(artifacts,descriptor);if(offer.DeliveryId!=request.DeliveryId)continue;
            if(!Matches(offer,manifest,session) || !offer.ManifestDigest.Span.SequenceEqual(proposal.ManifestDigest) || !CryptographicOperations.FixedTimeEquals(offer.Nonce.Span,request.Nonce.Span))throw Refused();
            var prior=artifacts.DescriptorHeld(manifest.BattleId,manifest.Fence,"offer-ack-"+i);
            BattleReadyOfferAckEvidenceV1 ack;
            if(prior is not null)ack=BattleReadyOfferJournal.ReadAck(artifacts,prior,descriptor);
            else
            {
                var sample=clock.Sample();if(sample.Utc.Ticks<offer.OfferedAtUtcTicks || sample.Utc.Ticks>=offer.DeadlineUtcTicks)throw Refused();
                ack=new(){Version=1,Offer=offer.Clone(),OfferSha256=ByteString.CopyFrom(Convert.FromHexString(descriptor.Sha256)),ReceivedAtUtcTicks=sample.Utc.Ticks,
                    ClockProcessId=sample.ProcessId,ClockTimestamp=sample.Timestamp,ClockFrequency=sample.Frequency};
                BattleReadyOfferJournal.Write(artifacts,manifest,"offer-ack-"+i,ack);
            }
            return new(){DeliveryId=offer.DeliveryId,ReceivedAtUtcTicks=ack.ReceivedAtUtcTicks};
        }
        throw Refused();
    }
    private async Task<(BattlePreparedManifestV1 Manifest,BattlePreparedProposal Proposal)> RequireOfferAsync(BattleTransportAdmission.Lease held,Session session,BattleScopeMessage scope,CancellationToken ct)
    {
        if(scope is null || !Uuid(scope.BattleId) || !Uuid(scope.RestoreBoundary) || scope.WorldInstanceId!=held.Cut.WorldId || scope.RestoreBoundary!=held.Cut.Boundary)throw Refused();
        var battle=await held.Db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b=>b.Id==scope.BattleId,ct)??throw Refused();
        var campaign=await held.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c=>c.Id==battle.CampaignId,ct);
        if(!battle.Active || battle.Phase!="Prepared" || battle.StartDigest is not null || !campaign.Active || campaign.Phase!="PreparingBattle"
            || battle.WorldId!=scope.WorldInstanceId || battle.Boundary!=scope.RestoreBoundary || battle.Attempt!=scope.Attempt || battle.Fence!=scope.Fence
            || campaign.AttackerCommanderId!=session.PlayerId || session.GuildVersion!=1 || campaign.ProductionCutUtc is not{} cut
            || clock.Sample().Utc<cut || clock.Sample().Utc>=campaign.ScheduledAtUtc.AddMinutes(5))throw Refused();
        var preparation=await GuildAuthorityEvidence.PreparationAsync(held.Db,battle.Id,ct);var executor=executors.RequireAssignedExecution(preparation.RuntimeAssignment);
        var proposal=new BattlePreparedJournal(artifacts).LatestHeld(battle.Id,battle.Fence)??throw Refused();var manifest=BattleManifestContracts.ReadPrepared(proposal.Manifest);
        await BattlePreparedAdmission.ValidateHeldAsync(held.Db,manifest,battle,campaign,executor,profiles,artifacts,sessions,ct,requireDefenderSession:false);
        BattleCommanderIdentity.RequireStillCurrent(sessions,session);
        if(!manifest.Attacker.SessionDigest.Equals(BattleCommanderIdentity.Digest(session)))throw Refused();
        return(manifest,proposal);
    }
    private static bool Matches(BattleReadyOfferEvidenceV1 offer,BattlePreparedManifestV1 manifest,Session session)=>offer.PlayerId==session.PlayerId
        && offer.SessionDigest.Equals(BattleCommanderIdentity.Digest(session)) && offer.MembershipJoinedUtcTicks==manifest.Attacker.MembershipJoinedUtcTicks
        && offer.ProposalSequence==manifest.ProposalSequence && offer.Scope.BattleId==manifest.BattleId && offer.Scope.Attempt==manifest.Attempt && offer.Scope.Fence==manifest.Fence
        && offer.Scope.WorldInstanceId==manifest.WorldInstanceId && offer.Scope.RestoreBoundary==manifest.RestoreBoundary && offer.DeadlineUtcTicks==manifest.ReadinessDeadlineUtcTicks;
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    private static OutpostEvidenceUnavailableException Refused()=>new("battle-ready-offer-refused");
}
