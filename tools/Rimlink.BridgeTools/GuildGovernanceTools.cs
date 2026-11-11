using Google.Protobuf;
using RimBridgeServer.Sdk;
using Rimlink.Client.Game;
using Rimlink.Client.Game.GuildCapture;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;

namespace Rimlink.BridgeTools;

public sealed class GuildGovernanceTools
{
    private static GuildWorldScope OwnScope()
    {
        var login=RimlinkSession.Login??throw new InvalidDataException("no session");
        var home=new GuildColonyScope{WorldInstanceId=RimlinkSession.World?.WorldInstanceId??"",RestoreBoundary=login.OutpostRestoreBoundary,
            PlayerId=login.Player.PlayerId,SaveGeneration=login.SaveGeneration,SaveWriteEpoch=login.SaveWriteEpoch};
        BattleGameContext.RequireIdleViewerHome(home);
        return new(){WorldInstanceId=home.WorldInstanceId,RestoreBoundary=home.RestoreBoundary};
    }
    private static object Reply(IMessage reply)=>new{success=true,reply=JsonFormatter.Default.Format(reply)};
    [Tool("rimlink/guild_outposts",Description="Read shipping guild outposts and this player's available old-owner resource obligations")]
    public async Task<object> List(IRimBridgeContext ctx)
    {
        try{var scope=await Bridge.OnMain(ctx,OwnScope);return Reply(await new GuildCaptureClient(RimlinkSession.Connection!).ListOutposts(scope,CancellationToken.None));}
        catch(Exception error){return Bridge.Fail(error.Message);}
    }
    [Tool("rimlink/guild_consent",Description="Preview automatic capture risk terms or accept their exact digest through the shipping outbox; preview alone never transfers ownership")]
    public async Task<object> Consent(IRimBridgeContext ctx,
        [ToolParameter(Required=true)]long allianceId,[ToolParameter(Required=true)]string outpostId,[ToolParameter(Required=true)]int defenceHourUtc,
        [ToolParameter(Description="Empty previews only; accepting requires the exact preview digest in lower-case hex")]string expectedTermsDigest="",
        [ToolParameter(Description="Stable request UUID; mandatory when accepting")]string requestId="")
    {
        try
        {
            var scope=await Bridge.OnMain(ctx,OwnScope);var api=new GuildCaptureClient(RimlinkSession.Connection!);
            var request=new GuildConsentRequest{Scope=scope,AllianceId=allianceId,OutpostId=outpostId,DefenceHourUtc=defenceHourUtc,ClientRequestId=requestId};
            if(expectedTermsDigest.Length==0)return Reply(await api.PreviewConsent(request,CancellationToken.None));
            if(!Guid.TryParseExact(requestId,"D",out _) || expectedTermsDigest.Length!=64)throw new InvalidDataException("exact digest and stable request UUID required");
            request.ExpectedTermsDigest=ByteString.CopyFrom(Convert.FromHexString(expectedTermsDigest));
            return Reply(await api.SubmitConsent(request,RimlinkSession.Outbox!,CancellationToken.None));
        }
        catch(Exception error){return Bridge.Fail(error.Message);}
    }
    [Tool("rimlink/guild_campaign",Description="Declare a shipping campaign through its durable outbox; normal notice, shield, profile and executor checks remain active")]
    public async Task<object> Campaign(IRimBridgeContext ctx,[ToolParameter(Required=true)]long allianceId,[ToolParameter(Required=true)]string outpostId,
        [ToolParameter(Required=true)]long ownerRevision,[ToolParameter(Required=true)]long commanderId,[ToolParameter(Required=true)]string requestId)
    {
        try
        {
            var scope=await Bridge.OnMain(ctx,OwnScope);
            return Reply(await new GuildCaptureClient(RimlinkSession.Connection!).SubmitCampaign(new(){Scope=scope,AllianceId=allianceId,OutpostId=outpostId,
                OwnerRevision=ownerRevision,CommanderId=commanderId,ClientRequestId=requestId},RimlinkSession.Outbox!,CancellationToken.None));
        }
        catch(Exception error){return Bridge.Fail(error.Message);}
    }
    [Tool("rimlink/guild_claim",Description="Claim a fixed recipient/count from shipping guild stock or an old-owner obligation; native delivery still requires canonical HOME receipts")]
    public async Task<object> Claim(IRimBridgeContext ctx,[ToolParameter(Required=true)]long allianceId,[ToolParameter(Required=true)]string originKind,
        [ToolParameter(Required=true)]string originId,[ToolParameter(Required=true)]long ownerRevision,[ToolParameter(Required=true)]long recipientId,
        [ToolParameter(Required=true)]long recipientGeneration,[ToolParameter(Required=true)]long recipientEpoch,[ToolParameter(Required=true)]int count,[ToolParameter(Required=true)]string requestId)
    {
        try
        {
            var scope=await Bridge.OnMain(ctx,OwnScope);
            return Reply(await new GuildCaptureClient(RimlinkSession.Connection!).SubmitClaim(new(){Scope=scope,AllianceId=allianceId,OriginKind=originKind,OriginId=originId,
                OwnerRevision=ownerRevision,RecipientId=recipientId,RecipientGeneration=recipientGeneration,RecipientEpoch=recipientEpoch,Count=count,ClientRequestId=requestId},RimlinkSession.Outbox!,CancellationToken.None));
        }
        catch(Exception error){return Bridge.Fail(error.Message);}
    }
    [Tool("rimlink/guild_defender_assign",Description="Assign a defending commander before Start through the leader's shipping outbox; no direct state change")]
    public async Task<object> AssignDefender(IRimBridgeContext ctx,[ToolParameter(Required=true)]string campaignId,
        [ToolParameter(Required=true)]long commanderId,[ToolParameter(Required=true)]string requestId)
    {
        try
        {
            if(!Guid.TryParseExact(requestId,"D",out _))throw new InvalidDataException("stable request UUID required");
            var scope=await Bridge.OnMain(ctx,OwnScope);
            return Reply(await new GuildCaptureClient(RimlinkSession.Connection!).SubmitDefenderCommander(new(){Scope=scope,CampaignId=campaignId,
                CommanderId=commanderId,ClientRequestId=requestId},RimlinkSession.Outbox!,CancellationToken.None));
        }
        catch(Exception error){return Bridge.Fail(error.Message);}
    }
}
