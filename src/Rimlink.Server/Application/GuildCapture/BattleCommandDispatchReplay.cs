namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleCommandJournal
{
    private void AcceptTransportFrame(byte kind,BinaryReader reader)
    {
        if(kind==4)AcceptControlledCommand(reader);
        else if(kind==5)AcceptDispatch(reader);
        else if(kind==6)AcceptCut(reader);
        else if(kind==7)
        {
            var id=new Guid(ReadExactly(reader,16));
            if(!cuts.TryGetValue(id,out var cut) || cut.Applied || pendingCut[Side(cut.Side)]!=id || HasPendingAtCut(cut))throw Invalid();
            cuts[id]=cut with{Applied=true};pendingCut[Side(cut.Side)]=null;
        }
        else if(kind==8)AcceptChannel(reader);
        else throw Invalid();
        if(reader.BaseStream.Position!=reader.BaseStream.Length)throw Invalid();
    }
    private void AcceptControlledCommand(BinaryReader reader)
    {
        int side=reader.ReadByte(),index=Side(side);long actor=reader.ReadInt64();var id=new Guid(ReadExactly(reader,16));long next=reader.ReadInt64();
        byte[] body=ReadBody(reader);var control=new BattleJournalControl(new Guid(ReadExactly(reader,16)).ToString("D"),reader.ReadInt64(),ReadExactly(reader,32),reader.ReadInt64());
        ValidateControl(control);
        if(body.Length>16320 || actor<=0 || id==Guid.Empty || requests.ContainsKey(id) || next!=sequence[index]+1 || next>24000 || pending[index]>=64
            || pendingCut[index] is not null || control.ConnectionEpoch<=cutConnection[index])throw Invalid();
        var command=new BattleJournalCommand(next,side,actor,id.ToString("N"),body,Control:control);
        requests.Add(id,command);commands.Add((side,next),command);sequence[index]=next;pending[index]++;unacknowledged.Add(new(side,next));
    }
    private void AcceptDispatch(BinaryReader reader)
    {
        var id=new Guid(ReadExactly(reader,16));int count=reader.ReadInt32();
        if(id==Guid.Empty || count is <1 or >8 || deliveries.ContainsKey(id) || outstandingDelivery is not null)throw Invalid();
        var keys=new BattleJournalKey[count];for(int i=0;i<count;i++)keys[i]=new(reader.ReadByte(),reader.ReadInt64());
        if(keys.Distinct().Count()!=count)throw Invalid();foreach(var key in keys)RequireDispatchable(key);
        string delivery=id.ToString("D");deliveries.Add(id,new(delivery,keys));outstandingDelivery=id;
        foreach(var key in keys)
        {
            var command=commands[(key.Side,key.Sequence)] with{DeliveryId=delivery};commands[(key.Side,key.Sequence)]=command;
            requests[Guid.ParseExact(command.RequestId,"N")]=command;
        }
    }
    private void AcceptCut(BinaryReader reader)
    {
        var id=new Guid(ReadExactly(reader,16));int side=reader.ReadByte(),index=Side(side);long through=reader.ReadInt64(),watermark=reader.ReadInt64();
        if(id==Guid.Empty || cuts.ContainsKey(id) || cuts.Count>=48000 || through<=cutConnection[index] || pendingCut[index] is not null || watermark!=sequence[index])throw Invalid();
        cuts.Add(id,new(id.ToString("D"),side,through,watermark));pendingCut[index]=id;cutConnection[index]=through;
    }
}
