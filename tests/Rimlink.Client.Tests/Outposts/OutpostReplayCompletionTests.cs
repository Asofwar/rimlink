using System.Buffers.Binary;
using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed partial class OutpostOutboxTests
{
    [Fact]
    public async Task Lost_commit_then_manifest_safety_hold_keeps_the_only_post_removal_journal_after_reopen()
    {
        using var fixture = await OutpostClientFixture.Create(commit: false);
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        var connection = session.Connection; var client = new OutpostClient(connection);
        var request = fixture.CommitRequest;
        var query = new OutpostOperationQuery { Scope = request.Scope.Clone(), ClientRequestId = request.ClientRequestId, IntentDigest = request.IntentDigest };
        Assert.Equal(OutpostOperationStatus.Prepared, (await client.Operation(query, default)).Status);
        await fixture.ConfirmIncompatibleManifest();
        Assert.Equal(OutpostOperationStatus.Prepared, (await client.Operation(query, default)).Status);
        Assert.Equal(OutpostOperationStatus.RecoveryRequired, (await client.Commit(request, default)).Status);
        var outbox = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(AcceptedMeta(fixture), accepted: true); outbox.MarkReplyUnknown(entry.Id);
        var clients = new OutboxClients(new EconomyClient(connection), null, null, Outposts: client);
        traffic.Paths.Clear();
        var report = await outbox.Replay(clients, default);
        Assert.Equal(0, report.Completed); Assert.Equal(1, report.Pending);
        Assert.Equal(entry.Payload, Assert.Single(outbox.Entries).Payload);
        Assert.Equal(new[] { "/rimlink.v1.Outposts/Operation", "/rimlink.v1.Outposts/Commit" }, traffic.Paths);
        var reopened = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        Assert.Equal(entry.Payload, Assert.Single(reopened.Entries).Payload); Assert.True(reopened.HasUnresolved);
        Assert.Equal(1, (await reopened.Replay(clients, default)).Pending);
        Assert.Equal(entry.Payload, Assert.Single(reopened.Entries).Payload);
    }

    [Theory]
    [InlineData("commit", "prepared")]
    [InlineData("commit", "recovery")]
    [InlineData("commit", "unspecified")]
    [InlineData("commit", "id")]
    [InlineData("commit", "digest")]
    [InlineData("commit", "scope")]
    [InlineData("cancel", "prepared")]
    [InlineData("cancel", "recovery")]
    [InlineData("cancel", "unspecified")]
    [InlineData("cancel", "id")]
    [InlineData("cancel", "digest")]
    [InlineData("cancel", "scope")]
    [InlineData("mutate", "prepared")]
    [InlineData("mutate", "recovery")]
    [InlineData("mutate", "unspecified")]
    [InlineData("mutate", "id")]
    [InlineData("mutate", "digest")]
    [InlineData("mutate", "scope")]
    [InlineData("mutate", "cancelled")]
    public async Task Replay_does_not_discard_nonterminal_or_foreign_operation_replies(string kind, string fault)
    {
        using var fixture = await OutpostClientFixture.Create(prepareOnly: true);
        var source = fixture.PrepareRequest; var proof = fixture.CommitRequest.PostRemoval;
        var reply = new OutpostOperationReply { Scope = source.Scope.Clone(), ClientRequestId = source.ClientRequestId,
            IntentDigest = source.IntentDigest, Status = kind == "cancel" ? OutpostOperationStatus.Cancelled : OutpostOperationStatus.Committed };
        switch (fault)
        {
            case "prepared": reply.Status = OutpostOperationStatus.Prepared; break;
            case "recovery": reply.Status = OutpostOperationStatus.RecoveryRequired; break;
            case "unspecified": reply.Status = OutpostOperationStatus.Unspecified; break;
            case "cancelled": reply.Status = OutpostOperationStatus.Cancelled; break;
            case "id": reply.ClientRequestId = Guid.NewGuid().ToString("N"); break;
            case "digest": reply.IntentDigest = ByteString.CopyFrom(new byte[32]); break;
            case "scope": reply.Scope.RestoreBoundary = "foreign"; break;
        }
        string endpoint = kind switch { "commit" => "Commit", "cancel" => "Cancel", _ => "Mutate" };
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler(), path => path == "/rimlink.v1.Outposts/" + endpoint ? OperationResponse(reply) : null);
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        IMessage request = kind switch
        {
            "commit" => fixture.CommitRequest,
            "cancel" => new OutpostCancelRequest { Scope = source.Scope.Clone(), ClientRequestId = source.ClientRequestId, IntentDigest = source.IntentDigest, CancellationProof = proof.Clone(), Kind = OutpostCancelKind.AbortNoRemoval },
            _ => new OutpostMutationRequest { Scope = source.Scope.Clone(), ClientRequestId = source.ClientRequestId, IntentDigest = source.IntentDigest, CanonicalSnapshot = proof.Clone() }
        };
        var outbox = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(AcceptedMeta(fixture), accepted: true);
        var clients = new OutboxClients(new EconomyClient(session.Connection), null, null, Outposts: new OutpostClient(session.Connection));
        var report = await outbox.Replay(clients, default);
        Assert.Equal(0, report.Completed); Assert.Equal(1, report.Pending);
        var reopened = new TransferOutbox(PathName, new ConsoleLog(), fixture.Scope.SaveGeneration, fixture.Scope.SaveWriteEpoch);
        Assert.Equal(entry.Payload, Assert.Single(reopened.Entries).Payload); Assert.True(reopened.HasUnresolved);
        Assert.Contains("/rimlink.v1.Outposts/" + endpoint, traffic.Paths);
    }

    private static HttpResponseMessage OperationResponse(OutpostOperationReply reply)
    {
        byte[] bytes = reply.ToByteArray(); byte[] frame = new byte[bytes.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), bytes.Length); bytes.CopyTo(frame, 5);
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Version = new Version(2, 0), Content = new ByteArrayContent(frame) };
        response.Content.Headers.ContentType = new("application/grpc"); response.TrailingHeaders.Add("grpc-status", "0");
        return response;
    }
}
