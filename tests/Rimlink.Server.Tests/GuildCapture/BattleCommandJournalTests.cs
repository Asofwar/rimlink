using System.Security.Cryptography;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleCommandJournalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rimlink-battle-journal-" + Guid.NewGuid().ToString("N"));
    private readonly BattleJournalScope scope = new(Guid.NewGuid().ToString("D"), "world", Guid.NewGuid().ToString("D"),
        "executor", 1, SHA256.HashData("start"u8));

    [Fact]
    public void Durable_commands_replay_the_same_sequence_and_ack_after_reopen()
    {
        var journal = Open();
        string request = Guid.NewGuid().ToString("N");
        Assert.Equal(1, journal.Append(1, 10, request, "move"u8));
        Assert.Equal(1, journal.Append(1, 10, request, "move"u8));
        Assert.Equal(1, journal.Append(2, 20, Guid.NewGuid().ToString("N"), "hold"u8));
        journal.Acknowledge(1, 1, 42, "accepted"u8);
        var reopened = Open();
        Assert.Equal(1, reopened.Append(1, 10, request, "move"u8));
        Assert.Equal(2, reopened.Append(1, 10, Guid.NewGuid().ToString("N"), "shoot"u8));
        Assert.Equal("accepted"u8.ToArray(), reopened.Read(1, 1).Acknowledgement);
        Assert.Equal(42, reopened.Read(1, 1).AcceptedTick);
        Assert.Equal(1, reopened.Pending(1));
        Assert.Equal(1, reopened.Pending(2));
    }

    [Fact]
    public void A_request_cannot_change_actor_side_or_payload()
    {
        var journal = Open();
        string request = Guid.NewGuid().ToString("N");
        journal.Append(1, 10, request, "move"u8);
        Assert.Throws<InvalidDataException>(() => journal.Append(1, 11, request, "move"u8));
        Assert.Throws<InvalidDataException>(() => journal.Append(2, 10, request, "move"u8));
        Assert.Throws<InvalidDataException>(() => journal.Append(1, 10, request, "hold"u8));
        journal.Acknowledge(1, 1, 7, "ok"u8);
        journal.Acknowledge(1, 1, 7, "ok"u8);
        Assert.Throws<InvalidDataException>(() => journal.Acknowledge(1, 1, 8, "ok"u8));
        Assert.Throws<InvalidDataException>(() => journal.Acknowledge(2, 1, 7, "ok"u8));
    }

    [Fact]
    public void Pending_and_payload_limits_are_enforced_before_any_append()
    {
        var journal = Open();
        for (int n = 0; n < 64; n++) journal.Append(1, 10, Guid.NewGuid().ToString("N"), "move"u8);
        long length = new FileInfo(PathOf()).Length;
        Assert.Throws<InvalidDataException>(() => journal.Append(1, 10, Guid.NewGuid().ToString("N"), "move"u8));
        Assert.Throws<InvalidDataException>(() => journal.Append(2, 20, Guid.NewGuid().ToString("N"), new byte[16385]));
        Assert.Throws<InvalidDataException>(() => journal.Append(0, 20, Guid.NewGuid().ToString("N"), "move"u8));
        Assert.Equal(length, new FileInfo(PathOf()).Length);
        journal.Acknowledge(1, 1, 1, "ok"u8);
        Assert.Equal(65, journal.Append(1, 10, Guid.NewGuid().ToString("N"), "move"u8));
    }

    [Fact]
    public void Seal_is_durable_and_idempotent_and_stops_new_commands()
    {
        var journal = Open();
        journal.Append(1, 10, Guid.NewGuid().ToString("N"), "move"u8);
        Assert.Throws<InvalidDataException>(() => journal.Seal());
        journal.Acknowledge(1, 1, 1, "ok"u8);
        var seal = journal.Seal();
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(PathOf())), seal.Digest);
        Assert.Equal(new FileInfo(PathOf()).Length, seal.SizeBytes);
        var reopened = Open();
        Assert.Equal(seal.Digest, reopened.Seal().Digest);
        Assert.Throws<InvalidDataException>(() => reopened.Append(1, 10, Guid.NewGuid().ToString("N"), "hold"u8));
        using var exclusive = File.Open(PathOf(), FileMode.Open, FileAccess.Read, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Corrupt_or_torn_record_is_refused_without_truncating_history(bool torn)
    {
        Open().Append(1, 10, Guid.NewGuid().ToString("N"), "move"u8);
        byte[] bytes = File.ReadAllBytes(PathOf());
        if (torn) Array.Resize(ref bytes, bytes.Length - 1);
        else bytes[^1] ^= 1;
        File.WriteAllBytes(PathOf(), bytes);
        Assert.Throws<InvalidDataException>(() => Open());
        Assert.Equal(bytes, File.ReadAllBytes(PathOf()));
    }

    [Fact]
    public void Stale_scope_and_seal_with_mutated_file_are_refused()
    {
        var journal = Open();
        Assert.Throws<InvalidDataException>(() => BattleCommandJournal.OpenHeld(root, scope with { Fence = 2 }));
        journal.Append(1, 10, Guid.NewGuid().ToString("N"), "move"u8);
        journal.Acknowledge(1, 1, 1, "ok"u8);
        byte[] bytes = File.ReadAllBytes(PathOf()); bytes[^1] ^= 1; File.WriteAllBytes(PathOf(), bytes);
        Assert.Throws<InvalidDataException>(() => journal.Seal());
    }

    [Fact]
    public void A_command_not_executed_before_terminal_is_closed_without_inventing_an_accepted_tick()
    {
        var journal = Open(); journal.Append(1, 10, Guid.NewGuid().ToString("N"), "move"u8);
        journal.Acknowledge(1, 1, null, "not-executed:terminal"u8);
        var reopened = Open();
        Assert.Null(reopened.Read(1, 1).AcceptedTick);
        Assert.Equal("not-executed:terminal"u8.ToArray(), reopened.Read(1, 1).Acknowledgement);
        Assert.Equal(0, reopened.Pending(1));
        reopened.Acknowledge(1, 1, null, "not-executed:terminal"u8);
        Assert.Throws<InvalidDataException>(() => reopened.Acknowledge(1, 1, 0, "not-executed:terminal"u8));
        Assert.Throws<InvalidDataException>(() => reopened.Acknowledge(1, 1, -1, "not-executed:terminal"u8));
        Assert.Equal(reopened.Seal().Digest, Open().Seal().Digest);
    }

    private BattleCommandJournal Open() => BattleCommandJournal.OpenHeld(root, scope);
    private string PathOf() => Path.Combine(root, scope.BattleId + ".commands");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
