using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleViewerMarkerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),"battle-view-marker-" + Guid.NewGuid().ToString("N"));
    private string FileName => Path.Combine(directory,"viewer.rlb");
    private static BattleViewerMarkerV1 Marker()
    {
        var battle = new BattleScopeMessage { WorldInstanceId = "world",RestoreBoundary = Guid.NewGuid().ToString("D"),BattleId = Guid.NewGuid().ToString("D"),Attempt = 1,Fence = 1 };
        return new() { Version = 1,EndpointId = "127.0.0.1_25561",Battle = battle,Home = new() { Scope = new() { WorldInstanceId = battle.WorldInstanceId,
            RestoreBoundary = battle.RestoreBoundary,PlayerId = 10,SaveGeneration = 2,SaveWriteEpoch = 3 },SnapshotId = Guid.NewGuid().ToString("N"),RawSha256 = ByteString.CopyFrom(new byte[32]) },
            HomeVersion = 7,ProfileDigest = ByteString.CopyFrom(new byte[32]),ManifestDigest = ByteString.CopyFrom(new byte[32]),Stage = BattleViewerStage.Entering };
    }
    [Fact]
    public void Cold_load_preserves_the_exact_home_and_only_allows_forward_handoff_stages()
    {
        var store = new BattleViewerMarkerStore(FileName); var marker = Marker(); store.Save(marker);
        Assert.Equal(marker,new BattleViewerMarkerStore(FileName).Read());
        var viewing = marker.Clone(); viewing.Stage = BattleViewerStage.Viewing; store.Save(viewing);
        Assert.Throws<InvalidDataException>(() => store.Save(marker));
        var different = viewing.Clone(); different.HomeVersion++;
        Assert.Throws<InvalidDataException>(() => store.Save(different));
        viewing.Stage = BattleViewerStage.ReturningHome; store.Save(viewing);
        Assert.Equal(viewing,store.Read());
        store.ClearAfterHome(viewing.Home,viewing.HomeVersion);
        Assert.Null(store.Read());
    }
    [Fact]
    public void A_failed_or_wrong_home_return_cannot_delete_the_recovery_marker()
    {
        var store = new BattleViewerMarkerStore(FileName); var marker = Marker(); store.Save(marker);
        Assert.Throws<InvalidDataException>(() => store.ClearAfterHome(marker.Home,marker.HomeVersion));
        marker.Stage = BattleViewerStage.ReturningHome; store.Save(marker);
        Assert.Throws<InvalidDataException>(() => store.ClearAfterHome(marker.Home,marker.HomeVersion+1));
        Assert.Equal(marker,store.Read());
    }
    [Fact]
    public void Torn_unknown_or_oversized_disk_evidence_is_preserved_and_refused()
    {
        Directory.CreateDirectory(directory);
        foreach (byte[] bytes in new[] { Marker().ToByteArray()[..^1],new byte[4097],[..Marker().ToByteArray(),0xA0,0x06,0x01] })
        {
            File.WriteAllBytes(FileName,bytes); var store = new BattleViewerMarkerStore(FileName);
            Assert.Throws<InvalidDataException>(() => store.Read());
            Assert.Throws<InvalidDataException>(() => store.Save(Marker()));
            Assert.Equal(bytes,File.ReadAllBytes(FileName));
        }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
}
