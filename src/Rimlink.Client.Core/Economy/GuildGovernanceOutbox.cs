using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Economy;

public sealed class GuildOperationJournal
{
    public long PlayerId{get;set;}
    public byte[] RuntimeTarget{get;set;}=[];
    public byte[] IntentDigest{get;set;}=[];
}

public sealed partial class TransferOutbox
{
    private static bool IsGuildOperation(OutboxKind kind)=>kind is OutboxKind.GuildConsent or OutboxKind.GuildCampaign or OutboxKind.GuildClaim or OutboxKind.GuildDefenderCommander;
    private static (OutboxKind Kind,GuildWorldScope Scope,string Id) GuildOperation(IMessage request)=>request switch
    {
        GuildConsentRequest r=>(OutboxKind.GuildConsent,r.Scope,r.ClientRequestId),
        GuildCampaignDeclaration r=>(OutboxKind.GuildCampaign,r.Scope,r.ClientRequestId),
        GuildDefenderCommanderAssignment r=>(OutboxKind.GuildDefenderCommander,r.Scope,r.ClientRequestId),
        GuildClaimRequest r=>(OutboxKind.GuildClaim,r.Scope,r.ClientRequestId),
        _=>throw new InvalidDataException("not a guild operation")
    };
    private static (GuildWorldScope Scope,string Id) ReadGuildOperation(OutboxEntry entry)
    {
        if(!IsGuildOperation(entry.Kind) || entry.Payload.Length is <1 or >4096 || entry.GuildOperation is not{PlayerId:>0,IntentDigest.Length:32} journal
            || journal.RuntimeTarget is not{Length:<=1024} || !SHA256.HashData(entry.Payload).AsSpan().SequenceEqual(journal.IntentDigest))throw InvalidGuildOperation();
        var r=GuildOperation(Decode(entry));
        if(r.Kind!=entry.Kind || r.Scope is null || string.IsNullOrWhiteSpace(r.Scope.WorldInstanceId) || r.Scope.WorldInstanceId.Length>256
            || r.Scope.WorldInstanceId.Any(char.IsControl) || !Uuid(r.Scope.RestoreBoundary) || !Uuid(r.Id))throw InvalidGuildOperation();
        if(journal.RuntimeTarget.Length!=0){var target=Parse(journal.RuntimeTarget,GuildRuntimeTarget.Parser);GuildRuntimeContracts.Require(target);}
        return(r.Scope,r.Id);
    }
    public OutboxEntry AddGuildOperation(IMessage request,LoginReply login)
    {
        var r=GuildOperation(request);var payload=request.ToByteArray();
        var entry=new OutboxEntry{Id=Guid.NewGuid().ToString("N"),Kind=r.Kind,Payload=payload,CreatedUtc=DateTime.UtcNow,Phase=OutboxPhase.Ready,
            SaveGeneration=generation,SaveWriteEpoch=epoch,GuildOperation=new(){PlayerId=login.Player.PlayerId,RuntimeTarget=login.GuildRuntime?.ToByteArray()??[],IntentDigest=SHA256.HashData(payload)}};
        var intent=ReadGuildOperation(entry);if(!GuildOperationReadyFor(entry,login))throw InvalidGuildOperation();
        lock(gate)
        {
            foreach(var old in entries.Where(e=>IsGuildOperation(e.Kind)))
            {
                var prior=ReadGuildOperation(old);
                if(prior.Id!=intent.Id || old.GuildOperation!.PlayerId!=login.Player.PlayerId)continue;
                if(old.Kind!=entry.Kind || !old.Payload.AsSpan().SequenceEqual(payload) || !old.GuildOperation.RuntimeTarget.AsSpan().SequenceEqual(entry.GuildOperation.RuntimeTarget))throw InvalidGuildOperation();
                return old;
            }
            entries.Add(entry);try{Save();}catch{entries.Remove(entry);throw;}return entry;
        }
    }
    private static bool GuildOperationReadyFor(OutboxEntry entry,LoginReply login)
    {
        try
        {
            var r=ReadGuildOperation(entry);
            return login.GuildVersion==GuildResourceContracts.CurrentVersion && entry.Phase==OutboxPhase.Ready
                && login.Player.PlayerId==entry.GuildOperation!.PlayerId && r.Scope.RestoreBoundary==login.OutpostRestoreBoundary
                && entry.GuildOperation.RuntimeTarget.AsSpan().SequenceEqual(login.GuildRuntime?.ToByteArray()??[]);
        }
        catch(Exception error)when(error is InvalidDataException or InvalidProtocolBufferException or ArgumentException){return false;}
    }
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id)&&id!=Guid.Empty&&id.ToString("D")==value;
    private static InvalidDataException InvalidGuildOperation()=>new("guild-operation-journal-invalid");
}
