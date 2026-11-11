using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Rimlink.BattlePrototype;
using Xunit;

namespace Rimlink.BattlePrototype.Tests;

public sealed class BattleTransportTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rimlink-battle-prototype-tests", Guid.NewGuid().ToString("N"));
    private static readonly BattleZone Arena = new(0, 0, 99, 99), Zone = new(45, 45, 55, 55);
    private readonly BattlePawn[] pawns = Enumerable.Range(1, 4).Select(i => new BattlePawn("a" + i, BattleSide.Attacker, 20, 40 + i))
        .Concat(Enumerable.Range(1, 4).Select(i => new BattlePawn("b" + i, BattleSide.Defender, 80, 40 + i))).ToArray();
    private CancellationTokenSource stopping = new();
    private readonly ManualClock clock = new();
    private BattleLedger ledger = null!;
    private BattleIdentity identity = null!;
    private BattleBinding[] bindings = [];
    private Task serving = Task.CompletedTask;

    public Task InitializeAsync()
    {
        identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, BattleContract.RulesDigest(Arena, Zone),
            new string('a', 64), BattleContract.Digest(pawns), 1);
        ledger = new(root, identity);
        byte[] bytes = [1, 2, 3]; File.WriteAllBytes(ledger.ArtifactPath(BattleArtifactKind.InitialSnapshot), bytes);
        var artifact = new BattleArtifact(BattleArtifactKind.InitialSnapshot, bytes.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        ledger.Start(BattleRole.Arbiter, new(identity, artifact, new(identity, 0, 100, new(100, 0, BattleOutcome.Ongoing, BattleEndReason.None), Arena, Zone, pawns)));
        bindings = Enum.GetValues<BattleRole>().Select(role => new BattleBinding(role,
            "battle-test-" + Guid.NewGuid().ToString("N"), new string((char)('a' + (int)role), 64), identity)).ToArray();
        serving = new BattleTransport(ledger, bindings, clock).Run(stopping.Token);
        return Task.CompletedTask;
    }

    private Task<BattleWireReply> Call(BattleRole role, string operation = "poll", BattleCommand? command = null, BattleFinalResult? result = null, BattleIdentity? id = null, string? capability = null)
    {
        var binding = bindings.Single(b => b.Role == role);
        return BattleTransport.Request(binding, new(operation, capability ?? binding.Capability, id ?? identity, Command: command, Result: result));
    }

    [Fact]
    public async Task IndependentSidesCannotUseEachOthersCapabilityOrPawn()
    {
        Assert.False((await Call(BattleRole.SideA, capability: bindings[2].Capability)).Success);
        Assert.False((await Call(BattleRole.SideA, "command", new(1, BattleCommandKind.MoveTo, "b1", 50, 50))).Success);
        var replies = await Task.WhenAll(Call(BattleRole.SideA, "command", new(1, BattleCommandKind.MoveTo, "a1", 50, 50)),
            Call(BattleRole.SideB, "command", new(1, BattleCommandKind.MoveTo, "b1", 50, 50)));
        Assert.All(replies, reply => Assert.True(reply.Success));
        var queued = (await Call(BattleRole.Arbiter)).Commands!;
        Assert.Equal(2, queued.Length);
        Assert.Contains(queued, c => c.Side == BattleSide.Attacker);
        Assert.Contains(queued, c => c.Side == BattleSide.Defender);
    }

    [Fact]
    public async Task UnknownAckReplaysSameSequenceWithoutDuplicatingTheOrder()
    {
        var command = new BattleCommand(1, BattleCommandKind.MoveTo, "a1", 50, 50);
        Assert.True((await Call(BattleRole.SideA, "command", command)).Success);
        Assert.True((await Call(BattleRole.SideA, "command", command)).Success);
        Assert.False((await Call(BattleRole.SideA, "command", command with { X = 51 })).Success);
        Assert.Single((await Call(BattleRole.Arbiter)).Commands!);
        Assert.Equal(2, (await Call(BattleRole.SideA)).NextSequence);
    }

    [Fact]
    public async Task WrongBattleOrRestoreFenceAndPlayerOutcomeAreRefused()
    {
        Assert.False((await Call(BattleRole.SideA, id: identity with { BattleId = Guid.NewGuid() })).Success);
        Assert.False((await Call(BattleRole.SideA, id: identity with { Boundary = Guid.NewGuid() })).Success);
        Assert.False((await Call(BattleRole.SideA, "final")).Success);
        Assert.Equal(0, ledger.State.OwnerChangedCount);
    }

    [Fact]
    public async Task OrdersAreBoundedAndHaveNoGenericJobOrSpeedEscape()
    {
        Assert.False((await Call(BattleRole.SideA, "command", new(1, (BattleCommandKind)99, "a1"))).Success);
        Assert.False((await Call(BattleRole.SideA, "command", new(1, BattleCommandKind.MoveTo, "a1", 100, 50))).Success);
        Assert.False((await Call(BattleRole.SideA, "command", new(2, BattleCommandKind.MoveTo, "a1", 50, 50))).Success);
        Assert.False((await Call(BattleRole.SideA, "execute_job")).Success);
        Assert.Empty((await Call(BattleRole.Arbiter)).Commands!);
    }

    [Fact]
    public async Task CommandsSurviveCoordinatorRestartWithoutAnotherExecution()
    {
        var command = new BattleCommand(1, BattleCommandKind.MoveTo, "a1", 50, 50);
        Assert.True((await Call(BattleRole.SideA, "command", command)).Success);
        stopping.Cancel(); await serving; ledger.Dispose(); stopping.Dispose();
        stopping = new(); ledger = new(root, identity);
        serving = new BattleTransport(ledger, bindings, clock).Run(stopping.Token);
        Assert.True((await Call(BattleRole.SideA, "command", command)).Success);
        Assert.Single((await Call(BattleRole.Arbiter)).Commands!);
    }

    [Fact]
    public async Task PartialPeerDoesNotRetireTheRoleListener()
    {
        var binding = bindings.Single(b => b.Role == BattleRole.SideA);
        using (var peer = new System.IO.Pipes.NamedPipeClientStream(".", binding.PipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await peer.ConnectAsync(2000);
            await peer.WriteAsync(new byte[] { 5, 0, 0, 0, (byte)'{' });
        }
        Assert.True((await Call(BattleRole.SideA)).Success);
        Assert.True((await Call(BattleRole.SideA)).Success);
    }

    [Fact]
    public async Task ReplyRemainsReadableWhenPeerIsScheduledLate()
    {
        var binding = bindings.Single(b => b.Role == BattleRole.SideA);
        using var peer = new System.IO.Pipes.NamedPipeClientStream(".", binding.PipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        await peer.ConnectAsync(2000);
        await BattleFrames.Write(peer, new BattleWireRequest("poll", binding.Capability, identity), BattleFrames.RequestLimit);
        await Task.Delay(50);
        Assert.True((await BattleFrames.Read<BattleWireReply>(peer, BattleFrames.ReplyLimit))!.Success);
    }

    [Fact]
    public async Task StoredViewCanBeReplayedAfterItsConnectionClosesWithoutReadingTheReply()
    {
        var binding = bindings.Single(b => b.Role == BattleRole.Arbiter);
        var original = (await Call(BattleRole.Arbiter)).View!;
        var view = original with { Sequence = 1, State = original.State with { Tick = 101 } };
        using (var peer = new System.IO.Pipes.NamedPipeClientStream(".", binding.PipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await peer.ConnectAsync(2000);
            await BattleFrames.Write(peer, new BattleWireRequest("view", binding.Capability, identity, View: view), BattleFrames.RequestLimit);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (ledger.Poll(BattleRole.SideA, identity).View!.Sequence != 1)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("view was not stored");
                await Task.Delay(1);
            }
        }
        var replay = await BattleFrames.Request(binding, new("view", binding.Capability, identity, View: view));
        Assert.True(replay.Success);
        Assert.Equal(1, replay.View!.Sequence);
        Assert.False((await BattleFrames.Request(binding, new("view", binding.Capability, identity,
            View: view with { State = view.State with { Tick = 102 } }))).Success);
    }

    [Fact]
    public async Task AppliedAcksAreArbiterOnlyImmutableAndReleasePendingCapacity()
    {
        await Call(BattleRole.SideA, "command", new(1, BattleCommandKind.MoveTo, "a1", 50, 50));
        var queued = Assert.Single((await Call(BattleRole.Arbiter)).Commands!);
        var ack = new BattleCommandAck(queued.Side, 1, queued.Digest, 101, true, null, new string('a', 64));
        foreach (var role in new[] { BattleRole.SideA, BattleRole.Arbiter })
        {
            var binding = bindings.Single(b => b.Role == role);
            var reply = await BattleTransport.Request(binding, new("ack", binding.Capability, identity, Ack: ack));
            Assert.Equal(role == BattleRole.Arbiter, reply.Success);
        }
        var arbiter = bindings[0];
        Assert.True((await BattleTransport.Request(arbiter, new("ack", arbiter.Capability, identity, Ack: ack))).Success);
        Assert.False((await BattleTransport.Request(arbiter, new("ack", arbiter.Capability, identity, Ack: ack with { Tick = 102 }))).Success);
        Assert.Empty((await Call(BattleRole.Arbiter)).Commands!);
    }

    [Fact]
    public async Task SideRateAndPendingLimitsDoNotBlockReceiptReplays()
    {
        for (int seq = 1; seq <= 20; seq++) Assert.True((await Call(BattleRole.SideA, "command", new(seq, BattleCommandKind.MoveTo, "a1", 50, 50))).Success);
        Assert.True((await Call(BattleRole.SideA, "command", new(20, BattleCommandKind.MoveTo, "a1", 50, 50))).Success);
        Assert.Equal("RateLimit", (await Call(BattleRole.SideA, "command", new(21, BattleCommandKind.MoveTo, "a1", 50, 50))).Code);
        for (int seq = 21; seq <= 64; seq++)
        {
            if ((seq - 1) % 20 == 0) clock.Advance();
            Assert.True((await Call(BattleRole.SideA, "command", new(seq, BattleCommandKind.MoveTo, "a1", 50, 50))).Success);
        }
        clock.Advance();
        Assert.Equal("QueueFull", (await Call(BattleRole.SideA, "command", new(65, BattleCommandKind.MoveTo, "a1", 50, 50))).Code);
        Assert.Equal(64, (await Call(BattleRole.Arbiter)).Commands!.Length);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long stamp;
        public override long GetTimestamp() => stamp;
        public override long TimestampFrequency => 1000;
        public void Advance() => stamp += 1001;
    }

    [Theory]
    [InlineData("{\"operation\":\"poll\",\"operation\":\"final\"}")]
    [InlineData("{\"operation\":\"poll\",\"unknown\":1}")]
    [InlineData("{\"operation\":\"poll\",\"command\":{\"sequence\":1,\"sequence\":2}}")]
    public async Task DuplicateOrUnknownJsonFieldsAreRejected(string json)
    {
        using var stream = Raw(json);
        await Assert.ThrowsAsync<JsonException>(() => BattleFrames.Read<BattleWireRequest>(stream, BattleFrames.RequestLimit));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(16385)]
    public async Task HostRejectsInvalidLengthsBeforeReadingPayload(int length)
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => BattleFrames.Read<BattleWireRequest>(stream, BattleFrames.RequestLimit));
    }

    [Fact]
    public async Task PartialFramesAreNotSuccessfulEof()
    {
        using var header = new MemoryStream(new byte[] { 1, 0 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => BattleFrames.Read<BattleWireRequest>(header, BattleFrames.RequestLimit));
        using var body = Raw("{}"); body.SetLength(body.Length - 1);
        await Assert.ThrowsAsync<EndOfStreamException>(() => BattleFrames.Read<BattleWireRequest>(body, BattleFrames.RequestLimit));
        using var empty = new MemoryStream(); Assert.Null(await BattleFrames.Read<BattleWireRequest>(empty, BattleFrames.RequestLimit));
    }

    [Fact]
    public async Task ExcessiveJsonDepthAndOversizedWritesAreRejected()
    {
        using var deep = Raw(new string('[', 18) + "0" + new string(']', 18));
        await Assert.ThrowsAnyAsync<JsonException>(() => BattleFrames.Read<BattleWireRequest>(deep, BattleFrames.RequestLimit));
        using var target = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => BattleFrames.Write(target, new string('x', 16384), BattleFrames.RequestLimit));
        Assert.Equal(0, target.Length);
    }

    private static MemoryStream Raw(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json); var stream = new MemoryStream();
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        stream.Write(header); stream.Write(bytes); stream.Position = 0; return stream;
    }

    public async Task DisposeAsync()
    {
        stopping.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(5)); ledger.Dispose(); stopping.Dispose();
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rimlink-battle-prototype-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
            throw new InvalidOperationException("test cleanup escaped its own root");
        Directory.Delete(root, recursive: true);
    }
}
