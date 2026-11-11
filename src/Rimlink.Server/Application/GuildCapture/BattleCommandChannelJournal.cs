using System.Text;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record BattleJournalChannel(string RequestId,int Side,long ActorId,string ClientChannelId,
    BattleJournalControl Control,byte[] TransportDigest,long? ExpectedPriorEpoch);

internal sealed partial class BattleCommandJournal
{
    private readonly Dictionary<Guid,BattleJournalChannel> channelRequests=[];
    private readonly BattleJournalChannel?[] channels=new BattleJournalChannel?[2];

    internal BattleJournalChannel? Channel(int side)
    {RequireHealthy();return channels[Side(side)] is {} channel?CopyChannel(channel):null;}

    internal BattleJournalChannel AttachChannel(int side,long actor,string requestId,string clientChannelId,string grantId,long grantEpoch,
        byte[] sessionDigest,byte[] transportDigest,long? expectedPriorEpoch)
    {
        int index=Side(side);RequireHealthy();
        if(actor<=0 || !Request(requestId,out var request) || !Guid.TryParseExact(clientChannelId,"D",out var client) || client==Guid.Empty
            || client.ToString("D")!=clientChannelId || transportDigest is not{Length:32} || expectedPriorEpoch<0)throw Invalid();
        var control=new BattleJournalControl(grantId,grantEpoch,sessionDigest,1);ValidateControl(control);
        if(channelRequests.TryGetValue(request,out var prior))
        {
            if(prior.Side!=side || prior.ActorId!=actor || prior.ClientChannelId!=clientChannelId || prior.Control.GrantId!=grantId
                || prior.Control.Epoch!=grantEpoch || !prior.Control.SessionDigest.AsSpan().SequenceEqual(sessionDigest)
                || !prior.TransportDigest.AsSpan().SequenceEqual(transportDigest) || prior.ExpectedPriorEpoch!=expectedPriorEpoch)throw Invalid();
            return CopyChannel(prior);
        }
        long previous=channels[index]?.Control.ConnectionEpoch??0;
        if(sealedJournal || channelRequests.Count>=48000 || previous==long.MaxValue || pendingCut[index] is not null
            || expectedPriorEpoch is {} expected && expected!=previous || previous>cutConnection[index]
            || channelRequests.Values.Any(c=>c.ClientChannelId==clientChannelId))throw Invalid();
        using var buffer=new MemoryStream();using var writer=new BinaryWriter(buffer,Encoding.UTF8,true);
        writer.Write((byte)8);writer.Write(request.ToByteArray());writer.Write((byte)side);writer.Write(actor);writer.Write(client.ToByteArray());
        writer.Write(Guid.ParseExact(grantId,"D").ToByteArray());writer.Write(grantEpoch);writer.Write(sessionDigest);writer.Write(previous+1);
        writer.Write(transportDigest);writer.Write(expectedPriorEpoch??-1);
        byte[] bytes=buffer.ToArray();AppendFrame(bytes);AcceptFrame(bytes);return CopyChannel(channelRequests[request]);
    }

    private void AcceptChannel(BinaryReader reader)
    {
        var request=new Guid(ReadExactly(reader,16));int side=reader.ReadByte(),index=Side(side);long actor=reader.ReadInt64();
        var client=new Guid(ReadExactly(reader,16));
        var control=new BattleJournalControl(new Guid(ReadExactly(reader,16)).ToString("D"),reader.ReadInt64(),ReadExactly(reader,32),reader.ReadInt64());
        byte[] transport=ReadExactly(reader,32);long expected=reader.ReadInt64(),previous=channels[index]?.Control.ConnectionEpoch??0;
        ValidateControl(control);
        if(request==Guid.Empty || client==Guid.Empty || actor<=0 || channelRequests.ContainsKey(request) || channelRequests.Count>=48000
            || previous==long.MaxValue || control.ConnectionEpoch!=previous+1 || pendingCut[index] is not null || previous>cutConnection[index]
            || expected< -1 || expected>=0 && expected!=previous || channelRequests.Values.Any(c=>c.ClientChannelId==client.ToString("D")))throw Invalid();
        var channel=new BattleJournalChannel(request.ToString("D"),side,actor,client.ToString("D"),control,transport,expected<0?null:expected);
        channelRequests.Add(request,channel);channels[index]=channel;
    }

    private static BattleJournalChannel CopyChannel(BattleJournalChannel channel)=>channel with
    {Control=channel.Control with{SessionDigest=channel.Control.SessionDigest.ToArray()},TransportDigest=channel.TransportDigest.ToArray()};
}
