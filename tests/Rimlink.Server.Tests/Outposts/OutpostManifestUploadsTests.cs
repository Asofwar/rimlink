using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostManifestUploadsTests
{
    [Fact]
    public async Task Chunks_stage_once_only_when_complete_and_never_confirm_implicitly()
    {
        using var f = await ManifestFixture.Create();
        var uploads = new OutpostManifestUploads(new ManualTimeProvider());
        byte[] bytes = (await f.Document()).ToByteArray();
        var first = Chunk(bytes, 0, 17);
        var partial = await uploads.StageAsync(Actor.Api, "admin:1", first, f.Service, default);
        Assert.False(partial.Complete); Assert.Equal(17, partial.ReceivedBytes);
        Assert.Empty(await f.H.Db.OutpostManifests.ToListAsync());
        Assert.Equal(17, (await uploads.StageAsync(Actor.Api, "admin:1", first, f.Service, default)).ReceivedBytes);
        var last = Chunk(bytes, 17, bytes.Length - 17, first.UploadId);
        var staged = await uploads.StageAsync(Actor.Api, "admin:1", last, f.Service, default);
        Assert.True(staged.Complete); Assert.Equal(bytes.Length, staged.ReceivedBytes);
        var row = await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync();
        Assert.False(row.Active); Assert.False(row.Confirmed); Assert.Equal(bytes, row.Document);
        var replay = await uploads.StageAsync(Actor.Api with { Name = "other audit label" }, "admin:1", last, f.Service, default);
        Assert.Equal(staged.ManifestId, replay.ManifestId);
        Assert.Equal(1, await f.H.Db.OutpostManifests.CountAsync());
    }

    [Theory]
    [InlineData("total")][InlineData("chunk")][InlineData("offset")][InlineData("hash")][InlineData("id")][InlineData("empty")]
    [InlineData("gap")][InlineData("overflow")][InlineData("truncated")][InlineData("not-protobuf")]
    public async Task Oversized_or_mismatched_chunks_do_not_stage(string defect)
    {
        using var f = await ManifestFixture.Create();
        var uploads = new OutpostManifestUploads(new ManualTimeProvider());
        byte[] bytes = (await f.Document()).ToByteArray();
        var chunk = Chunk(bytes, 0, bytes.Length);
        switch (defect)
        {
            case "total": chunk.TotalBytes = 24 * 1024 * 1024 + 1; break;
            case "chunk": chunk.TotalBytes = 256 * 1024 + 1; chunk.Data = ByteString.CopyFrom(new byte[256 * 1024 + 1]); break;
            case "offset": chunk.Offset = -1; break;
            case "hash": chunk.Sha256 = ByteString.CopyFrom(new byte[32]); break;
            case "id": chunk.UploadId = "not-an-id"; break;
            case "empty": chunk.Data = ByteString.Empty; break;
            case "gap": chunk.Offset = 1; chunk.TotalBytes++; break;
            case "overflow": chunk.Offset = int.MaxValue; break;
            case "truncated": chunk.Sha256 = ByteString.Empty; break;
            case "not-protobuf": chunk = Chunk([255], 0, 1); break;
        }
        await Assert.ThrowsAsync<RpcException>(() => uploads.StageAsync(Actor.Api, "admin:1", chunk, f.Service, default));
        Assert.Empty(await f.H.Db.OutpostManifests.ToListAsync());
    }

    [Theory]
    [InlineData("identity")][InlineData("digest")][InlineData("total")][InlineData("bytes")]
    public async Task Incomplete_upload_cannot_be_rebound_or_appended_by_another_admin(string change)
    {
        using var f = await ManifestFixture.Create();
        var uploads = new OutpostManifestUploads(new ManualTimeProvider());
        byte[] bytes = (await f.Document()).ToByteArray();
        var first = Chunk(bytes, 0, 17);
        await uploads.StageAsync(Actor.Api, "admin:1", first, f.Service, default);
        var next = Chunk(bytes, 17, bytes.Length - 17, first.UploadId);
        string identity = "admin:1";
        switch (change)
        {
            case "identity": identity = "admin:2"; break;
            case "digest": next.Sha256 = ByteString.CopyFrom(new byte[32]); break;
            case "total": next.TotalBytes++; break;
            case "bytes": next = first.Clone(); next.Data = ByteString.CopyFrom(new byte[17]); break;
        }
        await Assert.ThrowsAsync<RpcException>(() => uploads.StageAsync(Actor.Api, identity, next, f.Service, default));
        Assert.Empty(await f.H.Db.OutpostManifests.ToListAsync());
        Assert.True((await uploads.StageAsync(Actor.Api, "admin:1", Chunk(bytes, 17, bytes.Length - 17, first.UploadId), f.Service, default)).Complete);
    }

    [Fact]
    public async Task Non_admin_cannot_allocate_or_replay_upload()
    {
        using var f = await ManifestFixture.Create();
        var uploads = new OutpostManifestUploads(new ManualTimeProvider());
        byte[] bytes = (await f.Document()).ToByteArray();
        var chunk = Chunk(bytes, 0, bytes.Length);
        await uploads.StageAsync(Actor.Api, "admin:1", chunk, f.Service, default);
        var ex = await Assert.ThrowsAsync<RpcException>(() => uploads.StageAsync(new(1, 1, Role.Player, "user"), "admin:1", chunk, f.Service, default));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task A_rejected_complete_manifest_does_not_block_the_same_admin_from_uploading_a_corrected_document()
    {
        using var f = await ManifestFixture.Create();
        var uploads = new OutpostManifestUploads(new ManualTimeProvider());
        var document = await f.Document();
        byte[] corrected = document.ToByteArray();
        document.Tiles[0].BiomeDef = "UnknownBiomeForFixture";
        byte[] rejected = document.ToByteArray();

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            uploads.StageAsync(Actor.Api, "admin:1", Chunk(rejected, 0, rejected.Length), f.Service, default));
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Empty(await f.H.Db.OutpostManifests.ToArrayAsync());
        var retry = await uploads.StageAsync(Actor.Api, "admin:1", Chunk(corrected, 0, corrected.Length), f.Service, default);
        Assert.True(retry.Complete);
        Assert.Equal(corrected, (await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync()).Document);
    }

    [Fact]
    public async Task Incomplete_uploads_are_bounded_and_expire_on_monotonic_time_not_wall_time()
    {
        using var f = await ManifestFixture.Create();
        var time = new ManualTimeProvider(); var uploads = new OutpostManifestUploads(time);
        byte[] bytes = (await f.Document()).ToByteArray();
        await uploads.StageAsync(Actor.Api, "one", Chunk(bytes, 0, 1), f.Service, default);
        await Assert.ThrowsAsync<RpcException>(() => uploads.StageAsync(Actor.Api, "one", Chunk(bytes, 0, 1), f.Service, default));
        await uploads.StageAsync(Actor.Api, "two", Chunk(bytes, 0, 1), f.Service, default);
        time.Now += TimeSpan.FromDays(1);
        await Assert.ThrowsAsync<RpcException>(() => uploads.StageAsync(Actor.Api, "three", Chunk(bytes, 0, 1), f.Service, default));
        time.AdvanceMonotonic(TimeSpan.FromMinutes(11));
        Assert.Equal(1, (await uploads.StageAsync(Actor.Api, "three", Chunk(bytes, 0, 1), f.Service, default)).ReceivedBytes);
    }

    private static OutpostManifestChunk Chunk(byte[] bytes, int offset, int count, string? id = null) => new()
    {
        UploadId = id ?? Guid.NewGuid().ToString("D"), Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)),
        TotalBytes = bytes.Length, Offset = offset, Data = ByteString.CopyFrom(bytes, offset, count)
    };
}
