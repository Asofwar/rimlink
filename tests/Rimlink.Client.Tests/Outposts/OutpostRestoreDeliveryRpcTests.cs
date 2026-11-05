using Google.Protobuf;
using System.Text;
using System.Xml.Linq;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostRestoreDeliveryRpcTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Restored_mail_download_fresh_upload_and_exact_receipt_use_real_RPCs(bool rollback)
    {
        using var fixture = await OutpostClientFixture.Create();
        var oldRequest = await fixture.CloseForDelivery();
        await fixture.RestoreForDownload();
        if (rollback) await fixture.RollbackCurrentForDownload();
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        var connection = session.Connection; var saves = new SaveTransfer(connection); var economy = new EconomyClient(connection);
        var outposts = new OutpostClient(connection);
        var world = await connection.World.GetAsync(new Empty());
        string seed = world.Params.Seed;
        string directory = Path.Combine(Path.GetTempPath(), "rimlink-restored-delivery-rpc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "canonical.rws");
            var downloaded = (await saves.DownloadSnapshotAsync(file, default))!;
            using var raw = File.OpenRead(file); var recovery = SaveRecoveryData.ReadXml(raw, out byte[] hash); raw.Dispose();
            var loaded = OutpostLoadChecks.Prepare(downloaded.Header, recovery, hash, connection.Login);
            Assert.True(loaded.Allowed); Assert.True(loaded.RequiresFreshCapture);
            var metadata = loaded.Metadata!;
            Assert.NotEqual(oldRequest.Scope.RestoreBoundary, metadata.Scope.RestoreBoundary);
            var parcel = await economy.Get(oldRequest.ParcelId, default);
            Assert.Equal(oldRequest.ParcelId, parcel.Id); Assert.Equal(oldRequest.PayloadDigest, parcel.OutpostPayload.PayloadDigest);
            Assert.Equal(oldRequest.Scope, parcel.OutpostPayload.Scope);
            Assert.NotNull(parcel.OutpostRestoreAuthorization);
            Assert.True(OutpostDeliveryEvidence.MatchesScope(parcel, metadata.Scope));
            var fresh = await Upload(metadata, recovery, file, saves, seed, 1100);
            Assert.NotEqual(recovery.SnapshotId, fresh.Recovery!.SnapshotId);
            var request = new OutpostDeliveryRequest { Scope = metadata.Scope.Clone(), ParcelId = parcel.Id,
                PayloadDigest = parcel.OutpostPayload.PayloadDigest, ClientRequestId = Guid.NewGuid().ToString("N"),
                RestoreAuthorizationId = parcel.OutpostRestoreAuthorization.AuthorizationId,
                CanonicalSnapshot = new() { Scope = metadata.Scope.Clone(), SnapshotId = fresh.Recovery.SnapshotId, RawSha256 = ByteString.CopyFrom(fresh.RawSha256!) } };
            var reply = await outposts.BeginDelivery(request, default); Assert.False(reply.AckOnly);
            Assert.Equal(parcel.OutpostRestoreAuthorization, reply.RestoreAuthorization);
            Assert.Equal(reply, await outposts.BeginDelivery(request.Clone(), default));
            string branch = Guid.NewGuid().ToString("D");
            Assert.Equal(MaterializationDecision.Materialize, OutpostDeliveryEvidence.Decide(parcel, request, reply, metadata, branch, branch, false, true, true));
            var receipt = OutpostDeliveryEvidence.Receipt(parcel, request, reply);
            foreach (var registry in metadata.Registry.Where(r => parcel.OutpostPayload.PawnCustodyIds.Contains(r.PawnCustodyId)))
            { registry.Scope = metadata.Scope.Clone(); registry.Tombstone = false; registry.PawnReferenceToken = "Thing_ReturnedOverRpc"; }
            metadata.Receipts.Add(receipt); metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = reply.TransitionId,
                TransitionDigest = reply.TransitionDigest, PredecessorId = reply.PredecessorId });
            await Upload(metadata, recovery, file, saves, seed, 1200);
            Assert.True((await outposts.BeginDelivery(request, default)).AckOnly);
            Assert.Equal(MaterializationDecision.AckOnly, OutpostDeliveryEvidence.Decide(parcel, request, reply, metadata, branch, branch, true, true, true));
            await Upload(metadata.Clone(), recovery, file, saves, seed, 1300);
            Assert.True((await outposts.BeginDelivery(request, default)).AckOnly);
            Assert.DoesNotContain((await economy.Pending(default)).Parcels, p => p.Id == parcel.Id);
            Assert.Contains("/rimlink.v1.Saves/Upload", traffic.Paths);
            Assert.Contains("/rimlink.v1.Outposts/BeginDelivery", traffic.Paths);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<SaveMeta> Upload(OutpostSaveMetadata metadata, SaveRecoveryData original, string file, SaveTransfer saves, string seed, long tick)
    {
        byte[] packed = RecoveryFixtures.Save(tick, generation: metadata.Scope.SaveGeneration, epoch: metadata.Scope.SaveWriteEpoch,
            parcels: original.ParcelIds, removed: original.Removed, returned: original.Returned, outposts: new() { Metadata = metadata.Clone() });
        var document = XDocument.Parse(Encoding.UTF8.GetString(SaveFixtures.Decompress(packed)));
        document.Descendants("seedString").Single().Value = seed;
        await File.WriteAllTextAsync(file, document.ToString(), new UTF8Encoding(false));
        var meta = SaveMetaReader.Read(file); await saves.UploadAsync(file, meta, default); return meta;
    }
}
