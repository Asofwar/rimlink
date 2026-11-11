using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

// One live native process only; a failed/partial callback must never become a cold simulation replay.
public sealed class BattleRunningInbox
{
    private readonly BattlePreparedManifestV1 start;
    private readonly BattleScopeMessage scope;
    private readonly ByteString startDigest;
    private long position=-1;
    private byte[]? last;
    private bool failed;
    public BattleRunningInbox(BattlePreparedManifestV1 start)
    {
        this.start=BattleManifestContracts.ReadPrepared(start.ToByteArray());
        scope=new(){WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,BattleId=start.BattleId,Attempt=start.Attempt,Fence=start.Fence};
        startDigest=ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray()));
    }
    public bool Apply(BattleRunningWork work,Action<BattleQueuedCommand> enqueue,Action<BattleControlCut> cut,Action<IReadOnlyList<BattleRuntimeControlState>> project)
    {
        if(failed || work is null || work.Position<0 || work.CalculateSize()>16384 || !scope.Equals(work.Scope) || !startDigest.Equals(work.StartDigest))throw Invalid();
        if(work.Position<position)return false;
        byte[] bytes=work.ToByteArray();
        if(work.Position==position && (last is null || !bytes.AsSpan().SequenceEqual(last)))throw Invalid();
        if(work.Commands.Count>8 || work.Cuts.Count>2 || work.Controls.Count!=2 || work.Controls.Select(c=>c.Side).Distinct().Count()!=2
            || work.Commands.Select(c=>(c.Side,c.Sequence)).Distinct().Count()!=work.Commands.Count || work.Cuts.Select(c=>c.Side).Distinct().Count()!=work.Cuts.Count
            || work.Commands.Count>0 && !Uuid(work.DeliveryId))throw Invalid();
        foreach(var control in work.Controls)
            if(control.Side is not(BattleFighterSide.Attacker or BattleFighterSide.Defender) || control.ConnectionEpoch<0 || control.ControlEpoch<0
                || control.ConnectionEpoch==0 && (control.Present || control.ControlEpoch!=0 || control.GrantId!="")
                || control.ConnectionEpoch>0 && (!Uuid(control.GrantId) || control.ControlEpoch<=0))throw Invalid();
        foreach(var command in work.Commands)
        {
            if(command.Sequence is <1 or >24000 || command.ControlEpoch<=0 || command.ConnectionEpoch<=0 || !Uuid(command.GrantId)
                || command.DeliveryId!=work.DeliveryId || command.CommandDigest.Length!=32 || !SHA256.HashData(command.Command.Span).AsSpan().SequenceEqual(command.CommandDigest.Span))throw Invalid();
            _=BattleCommandContracts.Read(command.Command.Span,start,command.Side);
        }
        foreach(var row in work.Cuts)
        {
            var control=work.Controls.SingleOrDefault(c=>c.Side==row.Side);
            if(!Uuid(row.CutId) || row.ThroughConnectionEpoch<=0 || row.SequenceWatermark is <0 or >24000 || control is null || control.Present
                || control.ConnectionEpoch!=row.ThroughConnectionEpoch)throw Invalid();
        }
        try
        {
            foreach(var command in work.Commands)enqueue(command.Clone());
            foreach(var row in work.Cuts)cut(row.Clone());
            project(work.Controls.Select(c=>c.Clone()).ToArray());
            position=work.Position;last=bytes;return true;
        }
        catch{failed=true;throw;}
    }
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    private static InvalidDataException Invalid()=>new("battle-running-work-invalid");
}
