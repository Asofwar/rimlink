using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

internal static class BattleReadyOfferJournal
{
    internal static BattleArtifactDescriptor Write(BattleArtifactStore artifacts,BattlePreparedManifestV1 manifest,string kind,IMessage document)
    {
        byte[] bytes=document.ToByteArray();if(bytes.Length is <1 or >8192)throw Invalid();
        var descriptor=new BattleArtifactDescriptor(1,manifest.BattleId,manifest.WorldInstanceId,manifest.RestoreBoundary,manifest.ExecutorId,manifest.Fence,kind,bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));artifacts.AppendHeld(descriptor,0,bytes);return descriptor;
    }
    internal static BattleReadyOfferEvidenceV1 ReadOffer(BattleArtifactStore artifacts,BattleArtifactDescriptor descriptor)
    {
        byte[] bytes=artifacts.ReadDocumentHeld(descriptor,8192);BattleReadyOfferEvidenceV1 value;
        try{value=BattleReadyOfferEvidenceV1.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes);}catch(InvalidProtocolBufferException){throw Invalid();}
        if(!value.ToByteArray().AsSpan().SequenceEqual(bytes))throw Invalid();ValidateOffer(value);
        if(value.Scope.BattleId!=descriptor.BattleId || value.Scope.WorldInstanceId!=descriptor.WorldId || value.Scope.RestoreBoundary!=descriptor.Boundary || value.Scope.Fence!=descriptor.Fence)throw Invalid();
        return value;
    }
    internal static BattleReadyOfferAckEvidenceV1 ReadAck(BattleArtifactStore artifacts,BattleArtifactDescriptor descriptor,BattleArtifactDescriptor offerDescriptor)
    {
        byte[] bytes=artifacts.ReadDocumentHeld(descriptor,8192);BattleReadyOfferAckEvidenceV1 value;
        try{value=BattleReadyOfferAckEvidenceV1.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes);}catch(InvalidProtocolBufferException){throw Invalid();}
        var offer=ReadOffer(artifacts,offerDescriptor);
        if(!value.ToByteArray().AsSpan().SequenceEqual(bytes) || value.Version!=1 || !offer.Equals(value.Offer) || value.OfferSha256.Length!=32
            || Convert.ToHexStringLower(value.OfferSha256.Span)!=offerDescriptor.Sha256 || value.ReceivedAtUtcTicks<offer.OfferedAtUtcTicks
            || value.ReceivedAtUtcTicks>=offer.DeadlineUtcTicks || !ClockId(value.ClockProcessId) || value.ClockFrequency<=0
            || value.ClockProcessId==offer.ClockProcessId && (value.ClockFrequency!=offer.ClockFrequency || value.ClockTimestamp<offer.ClockTimestamp)
            || descriptor.BattleId!=offerDescriptor.BattleId || descriptor.WorldId!=offerDescriptor.WorldId || descriptor.Boundary!=offerDescriptor.Boundary
            || descriptor.ExecutorId!=offerDescriptor.ExecutorId || descriptor.Fence!=offerDescriptor.Fence)throw Invalid();
        return value;
    }
    private static void ValidateOffer(BattleReadyOfferEvidenceV1 value)
    {
        if(value.Version!=1 || value.Scope is not{} scope || !Uuid(scope.BattleId) || !Uuid(scope.RestoreBoundary) || string.IsNullOrWhiteSpace(scope.WorldInstanceId)
            || scope.WorldInstanceId.Length>256 || scope.Attempt<=0 || scope.Fence<=0 || value.ProposalSequence is <1 or >8 || value.ManifestDigest.Length!=32
            || !Uuid(value.DeliveryId) || !Uuid(value.ClientRequestId) || value.Nonce.Length!=32 || value.PlayerId<=0 || value.SessionDigest.Length!=32
            || value.MembershipJoinedUtcTicks<=0 || value.MembershipJoinedUtcTicks>DateTime.MaxValue.Ticks || value.OfferedAtUtcTicks<=0
            || value.DeadlineUtcTicks<=value.OfferedAtUtcTicks || value.DeadlineUtcTicks>DateTime.MaxValue.Ticks
            || value.DeadlineUtcTicks-value.OfferedAtUtcTicks>TimeSpan.FromMinutes(5).Ticks || !ClockId(value.ClockProcessId) || value.ClockFrequency<=0)throw Invalid();
    }
    private static bool ClockId(string value)=>Guid.TryParseExact(value,"N",out var id) && id!=Guid.Empty && id.ToString("N")==value;
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    private static InvalidDataException Invalid()=>new("battle-ready-offer-evidence-invalid");
}
