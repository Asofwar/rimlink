using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

// The only physical battle owns this cache under S → B; it retains no DB connection or file handle.
internal sealed class BattleTransportCache
{
    private string? identity;
    internal BattleCommandJournal Journal {get;private set;}=null!;
    internal BattlePreparedManifestV1 Start {get;private set;}=null!;
    internal BattleRunningViewBuffer Views {get;private set;}=null!;
    internal Dictionary<string,(byte[] Session,long Joined)> Bindings {get;}=[];
    internal Dictionary<(int Side,long Epoch),long> Presence {get;}=[];
    internal Queue<long>[] Accepted {get;}=[new(),new()];

    internal void BindHeld(BattleArtifactStore artifacts,BattleJournalScope scope,byte[] manifest)
    {
        string key=scope.BattleId+"/"+scope.WorldId+"/"+scope.Boundary+"/"+scope.ExecutorId+"/"+scope.Fence+"/"+Convert.ToHexString(scope.StartDigest);
        if(identity==key)return;
        Journal=BattleCommandJournal.OpenHeld(artifacts.CommandDirectoryHeld(scope.BattleId,scope.Fence),scope);
        Start=BattleManifestContracts.ReadPrepared(manifest);Views=new(Start);identity=key;Bindings.Clear();Presence.Clear();foreach(var queue in Accepted)queue.Clear();
    }
}
