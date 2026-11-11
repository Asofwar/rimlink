using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Grpc;

internal sealed partial class GuildCaptureGrpcService
{
    private static GuildMutationScope Scope(GuildWorldScope? scope)=>scope is not null&&!string.IsNullOrWhiteSpace(scope.WorldInstanceId)
        &&Guid.TryParseExact(scope.RestoreBoundary,"D",out var boundary)&&boundary!=Guid.Empty&&boundary.ToString("D")==scope.RestoreBoundary?new(scope.WorldInstanceId,scope.RestoreBoundary):throw new OutpostEvidenceUnavailableException("guild-scope-invalid");
    public override Task<GuildOutpostList> ListOutposts(GuildWorldScope request,ServerCallContext context)=>OfferCall(context,
        ()=>resources.ListOutpostsAsync(context.GetSession().PlayerId,Scope(request),context.CancellationToken));
    public override Task<GuildConsentReply> PreviewConsent(GuildConsentRequest request,ServerCallContext context)=>OfferCall<GuildConsentReply>(context,async()=>
    {
        var preview=await resources.PreviewConsentAsync(context.GetSession().PlayerId,request.AllianceId,Scope(request.Scope),request.OutpostId,request.DefenceHourUtc,context.CancellationToken);
        var t=preview.Terms;return new(){RulesVersion=(uint)t.Version,Scope=request.Scope.Clone(),OutpostId=t.OutpostId,OwnerRevision=t.Revision,AllianceId=t.AllianceId,
            LeaderId=t.LeaderId,ResourceDef=t.Resource==GuildResource.Steel?"Steel":"WoodLog",DefenceHourUtc=t.DefenceHourUtc,HourlyYield=t.HourlyYield,Capacity=t.Capacity,
            ProtectionHours=t.ProtectionHours,MaximumOutposts=t.MaximumOutposts,AutomaticCapture=t.AutomaticCapture,OldStockRetained=t.OldStockRemainsWithPreviousAlliance,TermsDigest=ByteString.CopyFrom(preview.Digest)};
    });
    public override Task<GuildOutpostMutationReply> AcceptConsent(GuildConsentRequest request,ServerCallContext context)=>OfferCall(context,async()=>
    {
        var scope=Scope(request.Scope);var session=context.GetSession();
        var receipt=await resources.AcceptConsentAsync(session.PlayerId,request.AllianceId,scope,request.ClientRequestId,request.OutpostId,request.DefenceHourUtc,request.ExpectedTermsDigest.ToByteArray(),context.CancellationToken);
        return new GuildOutpostMutationReply{OutpostId=receipt.OutpostId,OwnerRevision=receipt.Revision,Stock=receipt.Stock,TransitionId=receipt.TransitionId};
    });
    public override Task<GuildCampaignReply> DeclareCampaign(GuildCampaignDeclaration request,ServerCallContext context)=>OfferCall<GuildCampaignReply>(context,async()=>
    {
        var r=await resources.DeclareCampaignAsync(context.GetSession().PlayerId,new(Scope(request.Scope),request.ClientRequestId,request.AllianceId,request.OutpostId,
            request.OwnerRevision,request.CommanderId),context.CancellationToken);
        return new(){CampaignId=r.CampaignId,DeclaredAtUtcTicks=r.DeclaredAtUtc.Ticks,ScheduledAtUtcTicks=r.ScheduledAtUtc.Ticks,ExecutorId=r.ExecutorId,
            ProfileDigest=ByteString.CopyFrom(Convert.FromHexString(r.ProfileSha256)),RulesDigest=ByteString.CopyFrom(Convert.FromHexString(r.RulesSha256)),TransitionId=r.TransitionId};
    });
    public override Task<GuildClaimReply> ClaimResources(GuildClaimRequest request,ServerCallContext context)=>OfferCall<GuildClaimReply>(context,async()=>
    {
        var r=await resources.ClaimResourcesAsync(context.GetSession().PlayerId,new(Scope(request.Scope),request.ClientRequestId,request.AllianceId,request.OriginKind,request.OriginId,
            request.OwnerRevision,request.RecipientId,request.RecipientGeneration,request.RecipientEpoch,request.Count),context.CancellationToken);
        return new(){ClaimId=r.ClaimId,ParcelId=r.ParcelId,PayloadDigest=ByteString.CopyFrom(Convert.FromHexString(r.PayloadDigest)),TransitionId=r.TransitionId};
    });
    public override Task<GuildDefenderCommanderReply> AssignDefenderCommander(GuildDefenderCommanderAssignment request,ServerCallContext context)=>OfferCall<GuildDefenderCommanderReply>(context,async()=>
    {
        if(request.CalculateSize()>1024)throw new InvalidDataException("guild defender assignment too large");
        var r=await resources.AssignDefenderCommanderAsync(context.GetSession().PlayerId,Scope(request.Scope),request.ClientRequestId,
            request.CampaignId,request.CommanderId,context.CancellationToken);
        return new(){CampaignId=r.Request.CampaignId,CommanderId=r.Request.CommanderId,AssignedAtUtcTicks=r.AssignedAtUtc.Ticks,TransitionId=r.TransitionId};
    });
}
