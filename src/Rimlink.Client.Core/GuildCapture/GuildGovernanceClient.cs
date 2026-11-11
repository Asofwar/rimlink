using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;

namespace Rimlink.Client.GuildCapture;

public sealed partial class GuildCaptureClient
{
    private async Task<T> Submit<T>(IMessage request,GuildWorldScope scope,TransferOutbox outbox,Func<Task<T>> send,CancellationToken ct)
    {
        var login=Authentication(scope,ct);var entry=outbox.AddGuildOperation(request,login);
        try{var reply=await Call(login,send);outbox.Remove(entry.Id);return reply;}
        catch(global::Grpc.Core.RpcException error)when(ReferenceEquals(connection.Login,login)&&TransferOutbox.IsDefinitive(error))
        {outbox.Refuse(entry.Id,RpcErrorDetail.Read(error));throw;}
    }
    public Task<GuildOutpostMutationReply> SubmitConsent(GuildConsentRequest request,TransferOutbox outbox,CancellationToken ct)
    {
        request=request.Clone();if(request.ClientRequestId.Length==0)request.ClientRequestId=Guid.NewGuid().ToString("D");
        return Submit(request,request.Scope,outbox,()=>AcceptConsent(request,ct),ct);
    }
    public Task<GuildCampaignReply> SubmitCampaign(GuildCampaignDeclaration request,TransferOutbox outbox,CancellationToken ct)
    {
        request=request.Clone();if(request.ClientRequestId.Length==0)request.ClientRequestId=Guid.NewGuid().ToString("D");
        return Submit(request,request.Scope,outbox,()=>DeclareCampaign(request,ct),ct);
    }
    public Task<GuildClaimReply> SubmitClaim(GuildClaimRequest request,TransferOutbox outbox,CancellationToken ct)
    {
        request=request.Clone();if(request.ClientRequestId.Length==0)request.ClientRequestId=Guid.NewGuid().ToString("D");
        return Submit(request,request.Scope,outbox,()=>ClaimResources(request,ct),ct);
    }
    public Task<GuildDefenderCommanderReply> SubmitDefenderCommander(GuildDefenderCommanderAssignment request,TransferOutbox outbox,CancellationToken ct)
    {
        request=request.Clone();if(request.ClientRequestId.Length==0)request.ClientRequestId=Guid.NewGuid().ToString("D");
        if(!Guid.TryParseExact(request.CampaignId,"D",out var campaign) || campaign==Guid.Empty || request.CommanderId<=0)
            throw new InvalidDataException("invalid defender assignment");
        return Submit(request,request.Scope,outbox,()=>AssignDefenderCommander(request,ct),ct);
    }
    private LoginReply Authentication(GuildWorldScope? scope,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var login=connection.Login;
        if(login?.GuildVersion!=GuildResourceContracts.CurrentVersion || scope is null || string.IsNullOrWhiteSpace(scope.WorldInstanceId)
            || scope.WorldInstanceId.Length>256 || scope.WorldInstanceId.Any(char.IsControl) || scope.RestoreBoundary!=login.OutpostRestoreBoundary)
            throw new InvalidDataException("guild operation belongs to another authentication boundary");
        return login;
    }
    public Task<GuildOutpostList> ListOutposts(GuildWorldScope scope,CancellationToken ct)
    {
        scope=scope.Clone();var login=Authentication(scope,ct);
        return Call(login,()=>connection.GuildCaptureApi.ListOutpostsAsync(scope,deadline:DateTime.UtcNow.AddSeconds(10),cancellationToken:ct).ResponseAsync);
    }
    public Task<GuildConsentReply> PreviewConsent(GuildConsentRequest request,CancellationToken ct)
    {
        request=request.Clone();var login=Authentication(request.Scope,ct);
        return Call(login,()=>connection.GuildCaptureApi.PreviewConsentAsync(request,deadline:DateTime.UtcNow.AddSeconds(10),cancellationToken:ct).ResponseAsync);
    }
    public Task<GuildOutpostMutationReply> AcceptConsent(GuildConsentRequest request,CancellationToken ct)
    {
        request=request.Clone();var login=Authentication(request.Scope,ct);
        return Call(login,()=>connection.GuildCaptureApi.AcceptConsentAsync(request,deadline:DateTime.UtcNow.AddSeconds(30),cancellationToken:ct).ResponseAsync);
    }
    public Task<GuildCampaignReply> DeclareCampaign(GuildCampaignDeclaration request,CancellationToken ct)
    {
        request=request.Clone();var login=Authentication(request.Scope,ct);
        return Call(login,()=>connection.GuildCaptureApi.DeclareCampaignAsync(request,deadline:DateTime.UtcNow.AddSeconds(30),cancellationToken:ct).ResponseAsync);
    }
    public Task<GuildClaimReply> ClaimResources(GuildClaimRequest request,CancellationToken ct)
    {
        request=request.Clone();var login=Authentication(request.Scope,ct);
        return Call(login,()=>connection.GuildCaptureApi.ClaimResourcesAsync(request,deadline:DateTime.UtcNow.AddSeconds(30),cancellationToken:ct).ResponseAsync);
    }
    public Task<GuildDefenderCommanderReply> AssignDefenderCommander(GuildDefenderCommanderAssignment request,CancellationToken ct)
    {
        request=request.Clone();var login=Authentication(request.Scope,ct);
        return Call(login,()=>connection.GuildCaptureApi.AssignDefenderCommanderAsync(request,deadline:DateTime.UtcNow.AddSeconds(30),cancellationToken:ct).ResponseAsync);
    }
}
