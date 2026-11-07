using Google.Protobuf;
using Rimlink.Client;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.State;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed partial class OutpostOutboxTests
{
    private SaveMeta AcceptedMeta(OutpostClientFixture fixture)
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "selected.rws");
        File.WriteAllBytes(path, fixture.RawSave); return SaveMetaReader.Read(path);
    }
    [Fact]
    public async Task Lost_commit_reply_queries_same_operation_without_creating_another_intent()
    {
        using var fixture = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        var outbox = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        var request = fixture.CommitRequest; var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(AcceptedMeta(fixture), accepted: true);
        outbox.MarkReplyUnknown(entry.Id); traffic.Paths.Clear();
        var result = await outbox.Replay(new OutboxClients(new EconomyClient(connection), null, null,
            Outposts: new Rimlink.Client.Outposts.OutpostClient(connection)), default);
        Assert.Equal(1, result.Completed); Assert.Empty(outbox.Entries);
        Assert.Single(traffic.Paths, path => path == "/rimlink.v1.Outposts/Operation");
        Assert.DoesNotContain("/rimlink.v1.Outposts/Commit", traffic.Paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_reply_keeps_original_intent_until_canonical_commit_or_cancel(bool replyLost)
    {
        using var fixture = await OutpostClientFixture.Create(prepareOnly: true);
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        var outbox = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        var entry = outbox.Prepare(fixture.PrepareRequest); byte[] original = entry.Payload.ToArray();
        outbox.ReconcileSnapshot(AcceptedMeta(fixture), accepted: true);
        if (replyLost) outbox.MarkReplyUnknown(entry.Id);
        var clients = new OutboxClients(new EconomyClient(connection), null, null,
            Outposts: new Rimlink.Client.Outposts.OutpostClient(connection));
        var report = await outbox.Replay(clients, default);
        Assert.Equal(0, report.Completed); Assert.Equal(1, report.Pending);
        Assert.Equal(original, Assert.Single(outbox.Entries).Payload); Assert.True(outbox.HasUnresolved);
        var reopened = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        traffic.Paths.Clear();
        Assert.Equal(1, (await reopened.Replay(clients, default)).Pending);
        Assert.Equal(original, Assert.Single(reopened.Entries).Payload); Assert.True(reopened.HasUnresolved);
        Assert.Single(traffic.Paths, path => path == "/rimlink.v1.Outposts/Operation");
        Assert.DoesNotContain("/rimlink.v1.Outposts/Prepare", traffic.Paths);
    }

    [Fact]
    public async Task Old_boundary_is_quarantined_before_any_rpc()
    {
        using var fixture = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        var outbox = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        outbox.Prepare(fixture.CommitRequest); outbox.ReconcileSnapshot(AcceptedMeta(fixture), accepted: true);
        byte[] original = File.ReadAllBytes(PathName); connection.Login!.OutpostRestoreBoundary = "new-boundary"; traffic.Paths.Clear();
        var result = await outbox.Replay(new OutboxClients(new EconomyClient(connection), null, null,
            Outposts: new Rimlink.Client.Outposts.OutpostClient(connection)), default);
        Assert.Equal(0, result.Completed); Assert.Empty(outbox.Entries);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "outbox.json.before-*.json"))));
        Assert.DoesNotContain(traffic.Paths, path => path.StartsWith("/rimlink.v1.Outposts/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Protected_delivery_has_a_finite_budget_for_retained_restore_validation()
    {
        using var fixture = await OutpostClientFixture.Create();
        string? timeout = null;
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler())
        {
            Observe = call =>
            {
                if (call.RequestUri!.AbsolutePath == "/rimlink.v1.Outposts/BeginDelivery")
                    timeout = call.Headers.GetValues("grpc-timeout").Single();
            }
        };
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        var client = new Rimlink.Client.Outposts.OutpostClient(session.Connection);
        var request = await fixture.CloseForDelivery();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.BeginDelivery(request, cancelled.Token));
        Assert.Null(timeout);

        Assert.False((await client.BeginDelivery(request, default)).AckOnly);
        Assert.NotNull(timeout);
        decimal units = decimal.Parse(timeout[..^1], System.Globalization.CultureInfo.InvariantCulture);
        decimal seconds = units * (timeout[^1] switch
        {
            'H' => 3600m, 'M' => 60m, 'S' => 1m, 'm' => .001m, 'u' => .000001m, 'n' => .000000001m,
            _ => throw new InvalidDataException("invalid gRPC timeout unit"),
        });
        Assert.InRange(seconds, 60m, 120m);
    }

    [Fact]
    public async Task Accepted_canonical_receipt_is_ack_only_and_started_authorization_never_replays_spawn()
    {
        using var fixture = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        var client = new Rimlink.Client.Outposts.OutpostClient(connection); var request = await fixture.CloseForDelivery();
        var outbox = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(AcceptedMeta(fixture), accepted: true);
        var authorization = await client.BeginDelivery(request, default); Assert.False(authorization.AckOnly);
        outbox.RecordOutpostReply(entry.Id, authorization); traffic.Paths.Clear();
        var clients = new OutboxClients(new EconomyClient(connection), null, null, Outposts: client);
        Assert.Equal(1, (await outbox.Replay(clients, default)).Pending); Assert.Empty(traffic.Paths);
        byte[] accepted = await fixture.AcceptDelivery(request, authorization); string path = Path.Combine(directory, "canonical.rws");
        await File.WriteAllBytesAsync(path, accepted); outbox.ReconcileSnapshot(SaveMetaReader.Read(path), accepted: true);
        Assert.Equal(1, (await outbox.Replay(clients, default)).Completed); Assert.Empty(outbox.Entries);
        Assert.Single(traffic.Paths, p => p == "/rimlink.v1.Outposts/BeginDelivery");
        Assert.True((await client.BeginDelivery(request, default)).AckOnly);
    }

    [Fact]
    public async Task Old_outpost_upload_never_crosses_login_boundary_or_rewrites_bytes()
    {
        using var fixture = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        var meta = AcceptedMeta(fixture); string path = Path.Combine(directory, "selected.rws"); byte[] original = File.ReadAllBytes(path);
        connection.Login!.OutpostRestoreBoundary = "new-boundary"; traffic.Paths.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => new SaveTransfer(connection).UploadAsync(path, meta, default));
        Assert.DoesNotContain("/rimlink.v1.Saves/Upload", traffic.Paths); Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Temporarily_unavailable_outposts_keep_the_same_intent_replayable()
    {
        using var fixture = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        var outbox = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        var entry = outbox.Prepare(fixture.CommitRequest); outbox.ReconcileSnapshot(AcceptedMeta(fixture), accepted: true);
        var clients = new OutboxClients(new EconomyClient(connection), null, null, Outposts: new Rimlink.Client.Outposts.OutpostClient(connection));
        fixture.SetAvailable(false); Assert.Equal(1, (await outbox.Replay(clients, default)).Pending);
        Assert.Equal(OutboxPhase.Ready, Assert.Single(outbox.Entries).Phase);
        fixture.SetAvailable(true); Assert.Equal(1, (await outbox.Replay(clients, default)).Completed); Assert.Empty(outbox.Entries);
        Assert.Equal(entry.Payload, fixture.CommitRequest.ToByteArray());
    }

    [Fact]
    public async Task Independent_state_reads_retry_without_replaying_unknown()
    {
        using var fixture = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection; var state = new SharedState(); state.Outposts.BindScope(fixture.Scope);
        var outbox = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        var entry = outbox.Prepare(fixture.CommitRequest); outbox.ReconcileSnapshot(AcceptedMeta(fixture), accepted: true); outbox.MarkReplyUnknown(entry.Id);
        var recovery = new RecoveryCoordinator(connection, state, outbox, new ConsoleLog(), update => { update(); return Task.CompletedTask; });
        fixture.SetAvailable(false); Assert.False(await recovery.LoadStatesAsync(default));
        fixture.SetAvailable(true); Assert.True(await recovery.LoadStatesAsync(default)); Assert.Single(state.Outposts.Outposts);
        Assert.Single(outbox.Entries); Assert.DoesNotContain("/rimlink.v1.Outposts/Operation", traffic.Paths);
        Assert.DoesNotContain("/rimlink.v1.Outposts/Commit", traffic.Paths);
        Assert.Equal(2, traffic.Paths.Count(path => path == "/rimlink.v1.Outposts/State"));
    }
}
