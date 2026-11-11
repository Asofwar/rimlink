using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

// One savedata profile has one main-thread viewer handoff. A corrupt marker is recovery evidence, never disposable cache.
public sealed class BattleViewerMarkerStore(string path)
{
    private const int MaximumBytes = 4096;
    public BattleViewerMarkerV1? Read()
    {
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);
        if (file.Length is < 45 or > MaximumBytes + 44) throw Invalid();
        byte[] raw = new byte[(int)file.Length]; file.ReadExactly(raw);
        if (!raw.AsSpan(0,8).SequenceEqual("RLBVIEW1"u8)) throw Invalid();
        int length = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8,4));
        if (length is < 1 or > MaximumBytes || raw.Length != length + 44 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw.AsSpan(12,length)),raw.AsSpan(12+length,32))) throw Invalid();
        BattleViewerMarkerV1 value;
        try { value = BattleViewerMarkerV1.Parser.WithDiscardUnknownFields(true).ParseFrom(raw,12,length); }
        catch (InvalidProtocolBufferException) { throw Invalid(); }
        if (!value.ToByteArray().AsSpan().SequenceEqual(raw.AsSpan(12,length))) throw Invalid();
        Validate(value); return value;
    }
    public void Save(BattleViewerMarkerV1 marker)
    {
        marker = marker.Clone(); Validate(marker);
        if (Read() is { } previous)
        {
            if (marker.Stage < previous.Stage) throw Invalid();
            previous.Stage = marker.Stage;
            if (!previous.Equals(marker)) throw Invalid();
        }
        byte[] bytes = marker.ToByteArray(),raw = new byte[bytes.Length+44];
        "RLBVIEW1"u8.CopyTo(raw); BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8,4),bytes.Length);
        bytes.CopyTo(raw,12); SHA256.HashData(bytes).CopyTo(raw,bytes.Length+12);
        TransferOutbox.WriteDocument(path,raw);
    }
    public void ClearAfterHome(GuildSnapshotProof actualHome,int version)
    {
        var marker = Read() ?? throw Invalid();
        if (marker.Stage != BattleViewerStage.ReturningHome) throw Invalid();
        var guard = new BattleMapGuard(); guard.BeginViewer(marker.Battle,marker.Home,marker.HomeVersion); guard.RestoreViewerHome(actualHome,version);
        File.Delete(path);
    }
    private static void Validate(BattleViewerMarkerV1 value)
    {
        if (value.Version != 1 || string.IsNullOrWhiteSpace(value.EndpointId) || value.EndpointId.Length > 512 || value.EndpointId.Any(char.IsControl)
            || value.Stage is not (BattleViewerStage.Entering or BattleViewerStage.Viewing or BattleViewerStage.ReturningHome)
            || value.ProfileDigest.Length != 32 || value.ManifestDigest.Length != 32 || value.CalculateSize() > MaximumBytes) throw Invalid();
        var guard = new BattleMapGuard(); guard.BeginViewer(value.Battle,value.Home,value.HomeVersion);
    }
    private static InvalidDataException Invalid() => new("battle-view-marker-recovery-required");
}
