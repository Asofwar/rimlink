using System.Text;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record BattleJournalControl(string GrantId,long Epoch,byte[] SessionDigest,long ConnectionEpoch);
internal readonly record struct BattleJournalKey(int Side,long Sequence);
internal sealed record BattleJournalDispatch(string Id,IReadOnlyList<BattleJournalKey> Keys);
internal sealed record BattleJournalCut(string Id,int Side,long ThroughConnectionEpoch,long Watermark,bool Applied=false);

internal sealed partial class BattleCommandJournal
{
    private readonly Dictionary<Guid,BattleJournalDispatch> deliveries=[];
    private readonly Dictionary<Guid,BattleJournalCut> cuts=[];
    private readonly HashSet<BattleJournalKey> unacknowledged=[];
    private readonly Guid?[] pendingCut=new Guid?[2];
    private readonly long[] cutConnection=new long[2];
    private Guid? outstandingDelivery;
    private long position;
    internal long Position { get { RequireHealthy(); return position; } }

    internal long AppendControlled(int side,long actor,string request,ReadOnlySpan<byte> command,BattleJournalControl control)
    {
        int index=Side(side);RequireHealthy();ValidateControl(control);
        // Fixed Move/Attack/Draft documents fit the original frame budget together with their control binding.
        if(actor<=0 || command.Length is <1 or >16320 || !Request(request,out var id))throw Invalid();
        if(requests.TryGetValue(id,out var prior))
        {
            if(prior.Side!=side || prior.ActorId!=actor || !prior.Command.AsSpan().SequenceEqual(command) || !SameControl(prior.Control,control))throw Invalid();
            return prior.Sequence;
        }
        if(sealedJournal || pending[index]>=64 || sequence[index]>=24000 || pendingCut[index] is not null || control.ConnectionEpoch<=cutConnection[index])throw Invalid();
        using var buffer=new MemoryStream();using var writer=new BinaryWriter(buffer,Encoding.UTF8,true);
        writer.Write((byte)4);writer.Write((byte)side);writer.Write(actor);writer.Write(id.ToByteArray());writer.Write(sequence[index]+1);
        writer.Write(command.Length);writer.Write(command);writer.Write(Guid.ParseExact(control.GrantId,"D").ToByteArray());writer.Write(control.Epoch);
        writer.Write(control.SessionDigest);writer.Write(control.ConnectionEpoch);byte[] bytes=buffer.ToArray();AppendFrame(bytes);AcceptFrame(bytes);return sequence[index];
    }
    internal BattleJournalDispatch Dispatch(string deliveryId,IReadOnlyList<BattleJournalKey> keys)
    {
        RequireHealthy();if(!Request(deliveryId,out var id) || keys.Count is <1 or >8)throw Invalid();
        if(deliveries.TryGetValue(id,out var prior)){if(!prior.Keys.SequenceEqual(keys))throw Invalid();return prior with{Keys=prior.Keys.ToArray()};}
        if(sealedJournal || outstandingDelivery is not null || keys.Distinct().Count()!=keys.Count)throw Invalid();
        foreach(var key in keys)RequireDispatchable(key);
        using var buffer=new MemoryStream();using var writer=new BinaryWriter(buffer,Encoding.UTF8,true);
        writer.Write((byte)5);writer.Write(id.ToByteArray());writer.Write(keys.Count);foreach(var key in keys){writer.Write((byte)key.Side);writer.Write(key.Sequence);}
        byte[] bytes=buffer.ToArray();AppendFrame(bytes);AcceptFrame(bytes);return deliveries[id] with{Keys=keys.ToArray()};
    }
    internal BattleJournalDispatch? OutstandingDispatch()
    {RequireHealthy();return outstandingDelivery is {} id?deliveries[id] with{Keys=deliveries[id].Keys.ToArray()}:null;}
    internal BattleJournalCommand[] PendingCommands(int side)
    {Side(side);RequireHealthy();return unacknowledged.Where(k=>k.Side==side).OrderBy(k=>k.Sequence).Select(k=>Read(k.Side,k.Sequence)).ToArray();}
    internal bool ChannelClosed(int side,long connectionEpoch){RequireHealthy();return connectionEpoch<=cutConnection[Side(side)];}
    internal BattleJournalCut ReadCut(string cutId){RequireHealthy();return Request(cutId,out var id) && cuts.TryGetValue(id,out var cut)?cut:throw Invalid();}
    internal bool CutPending(int side){RequireHealthy();return pendingCut[Side(side)] is not null;}
    internal BattleJournalCut? PendingCut(int side){RequireHealthy();return pendingCut[Side(side)] is {} id?cuts[id]:null;}
    internal BattleJournalCut BeginCut(int side,long throughConnectionEpoch,string cutId)
    {
        int index=Side(side);RequireHealthy();if(!Request(cutId,out var id) || throughConnectionEpoch<=0)throw Invalid();
        if(cuts.TryGetValue(id,out var prior)){if(prior.Side!=side || prior.ThroughConnectionEpoch!=throughConnectionEpoch)throw Invalid();return prior;}
        if(sealedJournal || pendingCut[index] is not null || throughConnectionEpoch<=cutConnection[index] || cuts.Count>=48000)throw Invalid();
        using var buffer=new MemoryStream();using var writer=new BinaryWriter(buffer,Encoding.UTF8,true);
        writer.Write((byte)6);writer.Write(id.ToByteArray());writer.Write((byte)side);writer.Write(throughConnectionEpoch);writer.Write(sequence[index]);
        byte[] bytes=buffer.ToArray();AppendFrame(bytes);AcceptFrame(bytes);return cuts[id];
    }
    internal void AcknowledgeCut(string cutId)
    {
        RequireHealthy();if(!Request(cutId,out var id) || !cuts.TryGetValue(id,out var cut))throw Invalid();if(cut.Applied)return;
        if(sealedJournal || HasPendingAtCut(cut))throw Invalid();byte[] bytes=[7,..id.ToByteArray()];AppendFrame(bytes);AcceptFrame(bytes);
    }
    private bool HasPendingAtCut(BattleJournalCut cut)=>unacknowledged.Any(k=>k.Side==cut.Side && commands[(k.Side,k.Sequence)].Control is {} c && c.ConnectionEpoch<=cut.ThroughConnectionEpoch);
    private void RequireDispatchable(BattleJournalKey key)
    {
        int index=Side(key.Side);
        if(!commands.TryGetValue((key.Side,key.Sequence),out var command) || command.Control is null || command.Acknowledgement is not null || command.DeliveryId is not null
            || pendingCut[index] is not null || command.Control.ConnectionEpoch<=cutConnection[index])throw Invalid();
    }
    private void ResetTransport(){channelRequests.Clear();Array.Clear(channels);deliveries.Clear();cuts.Clear();unacknowledged.Clear();Array.Clear(pendingCut);Array.Clear(cutConnection);outstandingDelivery=null;position=0;}
    private void AfterTransportAcknowledgement()
    {
        if(outstandingDelivery is {} id && deliveries[id].Keys.All(k=>commands[(k.Side,k.Sequence)].Acknowledgement is not null))outstandingDelivery=null;
    }
    private static void ValidateControl(BattleJournalControl? value)
    {if(value is null || !Guid.TryParseExact(value.GrantId,"D",out var id) || id==Guid.Empty || id.ToString("D")!=value.GrantId || value.Epoch<=0 || value.SessionDigest is not{Length:32} || value.ConnectionEpoch<=0)throw Invalid();}
    private static bool SameControl(BattleJournalControl? a,BattleJournalControl b)=>a is not null && a.GrantId==b.GrantId && a.Epoch==b.Epoch
        && a.ConnectionEpoch==b.ConnectionEpoch && a.SessionDigest.AsSpan().SequenceEqual(b.SessionDigest);
}
