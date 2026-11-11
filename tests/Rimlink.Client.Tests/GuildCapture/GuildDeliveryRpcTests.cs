using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Net;
using Rimlink.Client.Tests.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class GuildDeliveryRpcTests
{
    [Fact]
    public async Task Production_handler_is_registered_and_refuses_unqualified_product_with_a_typed_error()
    {
        using var fixture = await OutpostClientFixture.Create();
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        Assert.Equal(0u, session.Connection.Login!.GuildVersion);
        var refused = await Assert.ThrowsAsync<RpcException>(() => session.Connection.GuildCaptureApi.BeginDeliveryAsync(new()).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Equal(ErrorCode.GuildUnavailable, EconomyClient.CodeOf(refused));
    }

    [Fact]
    public async Task Unadvertised_codec_and_foreign_colony_are_refused_before_sending()
    {
        using var fixture = await OutpostClientFixture.Create();
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); traffic.Paths.Clear();
        var (_, request, _, _) = GuildDeliveryEvidenceTests.Exchange(); var client = new GuildCaptureClient(session.Connection);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.BeginDelivery(request, default));
        session.Connection.Login!.GuildVersion = 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.BeginDelivery(request, default));
        Assert.DoesNotContain(traffic.Paths, p => p.StartsWith("/rimlink.v1.GuildCapture/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lost_reply_replays_exact_intent_and_only_a_real_accepted_receipt_completes_it()
    {
        using var fixture = await OutpostClientFixture.Create();
        var (parcel, request, authorization, metadata) = GuildDeliveryEvidenceTests.Exchange();
        var common = fixture.Scope;
        request.Scope = new() { WorldInstanceId = common.WorldInstanceId, PlayerId = common.OwnerPlayerId, SaveGeneration = common.SaveGeneration,
            SaveWriteEpoch = common.SaveWriteEpoch, RestoreBoundary = common.RestoreBoundary };
        request.BeforeSnapshot.Scope = request.Scope.Clone(); parcel.GuildResourcePayload.Recipient = request.Scope.Clone();
        request.PayloadDigest = ByteString.CopyFrom(SHA256.HashData(parcel.GuildResourcePayload.ToByteArray()));
        metadata.Scope = common.Clone(); metadata.GuildMetadata.Scope = request.Scope.Clone();
        authorization.Request = request.Clone(); authorization.IntentDigest = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray()));
        int calls = 0;
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler(), path =>
        {
            if (path != "/rimlink.v1.GuildCapture/BeginDelivery") return null;
            if (++calls == 1) throw new HttpRequestException("reply lost");
            return Response(authorization);
        });
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        session.Connection.Login!.GuildVersion = 1; // Transport fixture only; the production qualification remains closed.
        string directory = Path.Combine(Path.GetTempPath(), "rimlink-guild-rpc", Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "outbox.json");
            TransferOutbox Open() => new(path, new ConsoleLog(), common.SaveGeneration, common.SaveWriteEpoch);
            var box = Open(); var entry = box.PrepareGuildDelivery(parcel, request, Guid.NewGuid().ToString("D"));
            var meta = new SaveMeta("seed", "guild", 100, SaveGeneration: common.SaveGeneration,
                RawSha256: request.BeforeSnapshot.RawSha256.ToByteArray(), Recovery: new SaveRecoveryData { Version = 1,
                    Generation = common.SaveGeneration, WriteEpoch = common.SaveWriteEpoch, SnapshotId = request.BeforeSnapshot.SnapshotId,
                    Outposts = new() { Metadata = metadata } });
            box.ReconcileSnapshot(meta, true);
            var clients = new OutboxClients(new(session.Connection), null, null, Guild: new(session.Connection));
            Assert.Equal(1, (await box.Replay(clients, default)).Pending);
            box = Open(); Assert.True(box.Entries[0].Guild!.ReplyUnknown);
            Assert.Equal(request, TransferOutbox.Decode(box.Entries[0]));
            Assert.Equal(1, (await box.Replay(clients, default)).Pending); Assert.Equal(2, calls);
            Assert.Equal(1, (await box.Replay(clients, default)).Pending); Assert.Equal(2, calls); // Reply alone cannot close or respawn.
            var receipt = GuildDeliveryEvidence.Receipt(parcel, request, authorization); metadata.GuildMetadata.Receipts.Add(receipt);
            var post = meta with { RawSha256 = Enumerable.Repeat((byte)9, 32).ToArray(), Recovery = meta.Recovery! with { SnapshotId = Guid.NewGuid().ToString("N") } };
            box.ReconcileSnapshot(post, true);
            authorization.AckOnly = true; authorization.AcceptedSnapshot = new() { Scope = request.Scope.Clone(),
                SnapshotId = post.Recovery!.SnapshotId, RawSha256 = ByteString.CopyFrom(post.RawSha256!) };
            Assert.Equal(1, (await box.Replay(clients, default)).Completed); Assert.Empty(Open().Entries); Assert.Equal(3, calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static HttpResponseMessage Response(IMessage reply)
    {
        byte[] bytes = reply.ToByteArray(), frame = new byte[bytes.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), bytes.Length); bytes.CopyTo(frame, 5);
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Version = new(2, 0), Content = new ByteArrayContent(frame) };
        response.Content.Headers.ContentType = new("application/grpc"); response.TrailingHeaders.Add("grpc-status", "0"); return response;
    }
}
