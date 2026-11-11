using System.Security.Cryptography;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleArtifactTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rimlink-battle-artifacts", Guid.NewGuid().ToString("N"));
    private BattleArtifactStore Open() => new(directory);
    private static BattleArtifactDescriptor Descriptor(byte[] data) => new(1, Guid.NewGuid().ToString("D"), "world", Guid.NewGuid().ToString("D"),
        "arbiter-1", 1, "initial", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));

    [Fact]
    public void Completed_bytes_are_required_and_exact_chunk_replay_survives_reopen_and_lost_reply()
    {
        byte[] data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray(); var descriptor = Descriptor(data); var store = Open();
        Assert.Equal(300, store.AppendHeld(descriptor, 0, data.AsSpan(0, 300)).ReceivedBytes);
        Assert.Throws<InvalidDataException>(() => store.ValidateCompleteHeld(descriptor));
        Assert.Equal(300, Open().AppendHeld(descriptor, 0, data.AsSpan(0, 300)).ReceivedBytes);
        Assert.True(Open().AppendHeld(descriptor, 300, data.AsSpan(300)).Complete);
        var accepted = Open().ValidateCompleteHeld(descriptor);
        Assert.Equal(descriptor.Sha256, accepted.Sha256); Assert.Equal(data, Open().ReadHeld(descriptor, 0));
        Assert.True(Open().AppendHeld(descriptor, 300, data.AsSpan(300)).Complete);
        string moved = directory + "-moved"; Directory.Move(directory, moved); Directory.Move(moved, directory); // No handle survives S.
    }

    [Fact]
    public void Partial_tail_can_only_resume_after_exact_prefix_comparison_and_never_be_truncated()
    {
        byte[] data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray(); var descriptor = Descriptor(data);
        Open().AppendHeld(descriptor, 0, data.AsSpan(0, 333));
        byte[] conflict = data.AsSpan(300, 100).ToArray(); conflict[1]++;
        Assert.Throws<InvalidDataException>(() => Open().AppendHeld(descriptor, 300, conflict));
        Assert.Equal(333, Open().AppendHeld(descriptor, 0, data.AsSpan(0, 300)).ReceivedBytes);
        Assert.True(Open().AppendHeld(descriptor, 300, data.AsSpan(300)).Complete);
    }

    [Fact]
    public void Scope_change_gap_oversize_and_wrong_final_hash_never_publish_an_artifact()
    {
        byte[] data = new byte[8]; var descriptor = Descriptor(data); var store = Open();
        Assert.Throws<InvalidDataException>(() => store.AppendHeld(descriptor with { BattleId = "../outside" }, 0, data));
        Assert.Throws<InvalidDataException>(() => store.AppendHeld(descriptor with { SizeBytes = 67108865 }, 0, data));
        Assert.False(Directory.Exists(directory));
        store.AppendHeld(descriptor, 0, data.AsSpan(0, 4));
        Assert.Throws<InvalidDataException>(() => Open().AppendHeld(descriptor with { Boundary = Guid.NewGuid().ToString("D") }, 4, data.AsSpan(4)));
        Assert.Throws<InvalidDataException>(() => store.AppendHeld(descriptor, 5, data.AsSpan(5)));
        data[7] = 1;
        Assert.Throws<InvalidDataException>(() => store.AppendHeld(descriptor, 4, data.AsSpan(4)));
        Assert.Throws<InvalidDataException>(() => store.ValidateCompleteHeld(descriptor));
        Assert.Empty(Directory.GetFiles(directory, "*.artifact", SearchOption.AllDirectories));
    }

    [Fact]
    public void Corrupted_published_bytes_are_refused_before_a_financial_reference()
    {
        byte[] data = [1, 2, 3, 4]; var descriptor = Descriptor(data); var store = Open();
        store.AppendHeld(descriptor, 0, data); var proof = store.ValidateCompleteHeld(descriptor);
        File.WriteAllBytes(Path.Combine(directory, proof.RelativePath), [4, 3, 2, 1]);
        Assert.Throws<InvalidDataException>(() => Open().ValidateCompleteHeld(descriptor));
        Assert.Throws<InvalidDataException>(() => Open().AppendHeld(descriptor, 0, data));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
