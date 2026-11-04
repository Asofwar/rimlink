using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
namespace Rimlink.Client.Tests.Outposts;

public sealed class OrdinaryReplayTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rimlink-ordinary-replay", Guid.NewGuid().ToString("N"));
    private TransferOutbox Journal(OrdinaryCancelRequest request, bool returning = false)
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "outbox.json");
        // Recovery of the exact actually accepted fixture journal, not another local operation id.
        var entry = new OutboxEntry { Id = request.Capture.LocalOperationId, Kind = OutboxKind.Send,
            Payload = request.OriginalRequest.ToByteArray(), Phase = OutboxPhase.Ready, SaveGeneration = request.Scope.SaveGeneration,
            SaveWriteEpoch = request.Scope.SaveWriteEpoch, Ordinary = new() { Capture = request.Capture.ToByteArray(),
                RemovalProof = request.RemovalSnapshot.ToByteArray(), AcceptedProof = request.RemovalSnapshot.ToByteArray(),
                ReturnRequest = returning ? request.ToByteArray() : null, ReturnBranchId = returning ? Guid.NewGuid().ToString("D") : null } };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new[] { entry }, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));
        return new(path, new ConsoleLog(), request.Scope.SaveGeneration, request.Scope.SaveWriteEpoch);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Ordinary_replay_queries_retained_source_before_repeating_an_unknown_command(bool alreadyApplied)
    {
        var (fixture, request) = await OutpostClientFixture.CreateOrdinary(); using var owned = fixture;
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        await fixture.AddOrdinaryRecipientAccount();
        using var recipientTraffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var recipient = await OutpostTraffic.Connect(fixture, recipientTraffic, "ordinary-recipient");
        var economy = new EconomyClient(connection); var outposts = new OutpostClient(connection);
        if (alreadyApplied) await economy.Send(TransferRequest.Parser.ParseFrom(request.OriginalRequest), default);
        var box = Journal(request); traffic.Paths.Clear();
        var report = await box.Replay(new OutboxClients(economy, null, null, Outposts: outposts), default);
        Assert.Equal(1, report.Completed); Assert.Empty(box.Entries);
        string query = "/rimlink.v1.Outposts/OrdinaryOperation";
        Assert.Single(traffic.Paths, p => p == query);
        var relevant = traffic.Paths.Where(p => p == query || p == "/rimlink.v1.Transfer/Send").ToArray();
        Assert.Equal(query, relevant[0]);
        if (alreadyApplied) Assert.Single(relevant); else Assert.Equal(2, relevant.Length);
        Assert.DoesNotContain("/rimlink.v1.Outposts/BeginOrdinaryReturn", traffic.Paths);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Original_or_accepted_return_reply_never_spawns_or_discards_a_cold_journal(bool accepted)
    {
        var (fixture, request) = await OutpostClientFixture.CreateOrdinary(); using var owned = fixture;
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        var outposts = new OutpostClient(connection); var started = await outposts.BeginOrdinaryReturn(request, default);
        if (accepted) await fixture.AcceptOrdinaryReturn(request, started);
        var box = Journal(request, returning: true); var original = Assert.Single(box.Entries).Payload.ToArray(); traffic.Paths.Clear();
        var report = await box.Replay(new OutboxClients(new EconomyClient(connection), null, null, Outposts: outposts), default);
        Assert.Equal(1, report.Pending); Assert.Equal(original, Assert.Single(box.Entries).Payload);
        Assert.Single(traffic.Paths, p => p == "/rimlink.v1.Outposts/OrdinaryOperation");
        Assert.DoesNotContain("/rimlink.v1.Transfer/Send", traffic.Paths);
        Assert.DoesNotContain("/rimlink.v1.Outposts/BeginOrdinaryReturn", traffic.Paths);
    }
    [Fact]
    public async Task Unavailable_ordinary_decision_keeps_original_journal_without_business_retry()
    {
        var (fixture, request) = await OutpostClientFixture.CreateOrdinary(); using var owned = fixture;
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var box = Journal(request);
        fixture.SetAvailable(false); traffic.Paths.Clear();
        var report = await box.Replay(new OutboxClients(new EconomyClient(session.Connection), null, null, Outposts: new OutpostClient(session.Connection)), default);
        Assert.Equal(1, report.Pending); Assert.Equal(OutboxPhase.Ready, Assert.Single(box.Entries).Phase);
        Assert.DoesNotContain("/rimlink.v1.Transfer/Send", traffic.Paths);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
