using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class GuildGovernanceOutboxTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"rimlink-guild-governance-"+Guid.NewGuid().ToString("N"));
    private TransferOutbox Open()=>new(Path.Combine(directory,"outbox.json"),new ConsoleLog(),4,8);
    private static (LoginReply Login,IMessage Request) Intent(string kind)
    {
        var login=new LoginReply{GuildVersion=1,Player=new(){PlayerId=12},SaveGeneration=4,SaveWriteEpoch=8,OutpostRestoreBoundary=Guid.NewGuid().ToString("D"),
            GuildRuntime=new(){Mode=GuildRuntimeMode.IsolatedAcceptanceV1,StandId=Guid.NewGuid().ToString("D")}};
        var scope=new GuildWorldScope{WorldInstanceId="world",RestoreBoundary=login.OutpostRestoreBoundary};string id=Guid.NewGuid().ToString("D");
        IMessage request=kind switch
        {
            "consent"=>new GuildConsentRequest{Scope=scope,ClientRequestId=id,AllianceId=3,OutpostId=Guid.NewGuid().ToString("D"),ExpectedTermsDigest=ByteString.CopyFrom(new byte[32])},
            "campaign"=>new GuildCampaignDeclaration{Scope=scope,ClientRequestId=id,AllianceId=3,OutpostId=Guid.NewGuid().ToString("D"),CommanderId=12,OwnerRevision=1},
            "defender"=>new GuildDefenderCommanderAssignment{Scope=scope,ClientRequestId=id,CampaignId=Guid.NewGuid().ToString("D"),CommanderId=12},
            _=>new GuildClaimRequest{Scope=scope,ClientRequestId=id,AllianceId=3,OriginKind="Outpost",OriginId=Guid.NewGuid().ToString("D"),OwnerRevision=1,RecipientId=12,RecipientGeneration=4,RecipientEpoch=8,Count=10}
        };
        return(login,request);
    }
    [Theory]
    [InlineData("consent")][InlineData("campaign")][InlineData("defender")][InlineData("claim")]
    public void Cold_journal_keeps_the_exact_request_and_deduplicates_without_reusing_an_id_for_changed_intent(string kind)
    {
        var(login,request)=Intent(kind);var box=Open();var entry=box.AddGuildOperation(request,login);
        Assert.Equal(entry.Id,box.AddGuildOperation(request,login).Id);Assert.False(box.HasUnresolved);
        var cold=Open();var restored=Assert.Single(cold.Entries);Assert.Equal(request.ToByteString(),TransferOutbox.Decode(restored).ToByteString());
        Assert.True(TransferOutbox.ReadyFor(restored,login));
        switch(request){case GuildConsentRequest r:r.DefenceHourUtc++;break;case GuildCampaignDeclaration r:r.CommanderId++;break;case GuildDefenderCommanderAssignment r:r.CommanderId++;break;case GuildClaimRequest r:r.Count++;break;}
        Assert.Throws<InvalidDataException>(()=>cold.AddGuildOperation(request,login));Assert.Single(Open().Entries);
    }
    [Theory]
    [InlineData("player")][InlineData("generation")][InlineData("epoch")][InlineData("boundary")][InlineData("stand")][InlineData("ordinary")][InlineData("version")]
    public void A_previous_player_or_acceptance_boundary_cannot_replay_a_pending_claim(string fault)
    {
        var(login,request)=Intent("claim");var entry=Open().AddGuildOperation(request,login);var next=login.Clone();
        switch(fault)
        {
            case "player":next.Player.PlayerId++;break;case "generation":next.SaveGeneration++;break;case "epoch":next.SaveWriteEpoch++;break;
            case "boundary":next.OutpostRestoreBoundary=Guid.NewGuid().ToString("D");break;case "stand":next.GuildRuntime.StandId=Guid.NewGuid().ToString("D");break;
            case "ordinary":next.GuildRuntime=null;break;case "version":next.GuildVersion=0;break;
        }
        Assert.False(TransferOutbox.ReadyFor(entry,next));Assert.Single(Open().Entries);
    }
    [Fact]
    public void A_changed_payload_is_preserved_and_refused_before_replay()
    {
        var(login,request)=Intent("claim");var entry=Open().AddGuildOperation(request,login);entry.Payload[^1]^=1;
        Assert.False(TransferOutbox.ReadyFor(entry,login));
    }

    [Fact]
    public void A_confirmed_claim_conflict_is_discarded_while_authority_unavailability_remains_retryable()
    {
        static RpcException Reply(ErrorCode code, string key) => new(new Status(StatusCode.FailedPrecondition, key),
            new Metadata { { "rimlink-error-bin", new ErrorDetail { Code = code, Key = key }.ToByteArray() } });

        var (login, request) = Intent("claim");
        var box = Open();
        var entry = box.AddGuildOperation(request, login);
        Assert.False(TransferOutbox.IsDefinitive(Reply(ErrorCode.GuildUnavailable, "Guild.Unavailable")));
        var conflict = Reply(ErrorCode.OutpostConflict, "Guild.Claim");
        Assert.True(TransferOutbox.IsDefinitive(conflict));
        box.Refuse(entry.Id, RpcErrorDetail.Read(conflict));
        Assert.Empty(Open().Entries);
    }
    [Fact]
    public void Personal_snapshot_operation_lists_cannot_acknowledge_a_guild_claim()
    {
        var(login,request)=Intent("claim");var box=Open();var entry=box.AddGuildOperation(request,login);
        box.ReconcileSnapshot(new Rimlink.Protocol.SaveRecoveryData{Version=1,Generation=4,WriteEpoch=8,SnapshotId=Guid.NewGuid().ToString("N"),Returned=[entry.Id],Removed=[entry.Id]},accepted:true,selected:true);
        Assert.Single(Open().Entries);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
