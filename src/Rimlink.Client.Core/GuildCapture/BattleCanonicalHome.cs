using Google.Protobuf;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public static class BattleCanonicalHome
{
    public static void RequireRetained(BattleViewerMarkerV1 marker,string endpointId,string file,SaveInfo? accepted,GuildColonyScope current)
    {
        if(marker is null || marker.EndpointId!=endpointId || accepted?.Version!=marker.HomeVersion
            || !ReadAccepted(file,accepted,current).Equals(marker.Home))throw Invalid();
    }
    public static GuildSnapshotProof ReadAccepted(string file,SaveInfo? accepted,GuildColonyScope current)
    {
        GuildResourceContracts.ValidateScope(current);
        if(accepted is null || !accepted.IsCurrent || accepted.Version<=0 || accepted.RecoveryVersion!=SaveRecoveryData.CurrentVersion
            || accepted.OutpostVersion!=OutpostRecoveryData.CurrentVersion || accepted.SaveWriteEpoch!=current.SaveWriteEpoch
            || accepted.OutpostRestoreBoundary!=current.RestoreBoundary)throw Invalid();
        var proof=new GuildSnapshotProof{Scope=current.Clone(),SnapshotId=accepted.SnapshotId,RawSha256=accepted.RawSha256};
        RequireFile(file,proof);return proof;
    }
    public static void RequireFile(string file,GuildSnapshotProof proof)
    {
        if(proof is null || proof.RawSha256.Length!=32 || !Guid.TryParseExact(proof.SnapshotId,"N",out var id) || id==Guid.Empty)throw Invalid();
        GuildResourceContracts.ValidateScope(proof.Scope);BattleExecutorSettings.RequireNoLinks(file);
        using var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(held.Length is <1 or >67108864)throw Invalid();
        var actual=SaveMetaReader.Read(file);
        if(actual.Recovery is not{Version:1} recovery || recovery.SnapshotId!=proof.SnapshotId || recovery.Generation!=proof.Scope.SaveGeneration
            || recovery.WriteEpoch!=proof.Scope.SaveWriteEpoch || actual.RawSha256 is not{Length:32} hash || !hash.AsSpan().SequenceEqual(proof.RawSha256.Span)
            || recovery.Outposts.Metadata.GuildMetadata is not{SchemaVersion:1} guild || !guild.Scope.Equals(proof.Scope))throw Invalid();
    }
    private static InvalidDataException Invalid()=>new("battle-home-requires-accepted-canonical-snapshot");
}
