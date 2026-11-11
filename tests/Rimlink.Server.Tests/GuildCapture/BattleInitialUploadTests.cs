using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleInitialUploadTests
{
    [Fact]
    public async Task A_complete_initial_file_is_replayable_but_does_not_create_a_financial_reference_or_Start()
    {
        using var f = await GuildFixture.Create(true); var (executor, _) = await BattleExecutorWorkTests.Prepare(f);
        var reader = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, f.Battles);
        var service = new BattleExecutorWorkService(f.Executors, reader, f.Clock, new BattleArtifactStore(f.H.Data.Archive));
        var work = await service.PollAsync(executor, default); byte[] data = Enumerable.Range(0, 1000).Select(n => (byte)n).ToArray();
        string head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        var chunk = new BattleArtifactChunk { Executor = work.Lease.Clone(), Scope = work.Preparing.Scope.Clone(), TotalBytes = data.Length,
            Sha256 = ByteString.CopyFrom(SHA256.HashData(data)), Offset = 0, Data = ByteString.CopyFrom(data, 0, 500) };
        Assert.False((await service.UploadInitialAsync(chunk, default)).Complete);
        Assert.Equal(500, (await service.UploadInitialAsync(chunk, default)).ReceivedBytes);
        chunk.Offset = 500; chunk.Data = ByteString.CopyFrom(data, 500, 500);
        var final = await service.UploadInitialAsync(chunk, default);
        Assert.True(final.Complete); Assert.Equal(final, await service.UploadInitialAsync(chunk, default));
        Assert.Equal(chunk.Scope, final.Artifact.Scope); Assert.Equal(chunk.Sha256, final.Artifact.Sha256);
        Assert.Equal(data, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(f.H.Data.Archive, "guild-battles"), "*.artifact", SearchOption.AllDirectories))));
        Assert.Empty(await f.H.Db.Set<GuildBattleArtifact>().ToArrayAsync());
        Assert.Null((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).StartDigest);
        Assert.Equal(head, (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
    }

    [Theory]
    [InlineData("fence")][InlineData("boundary")][InlineData("lease")][InlineData("oversize")]
    public async Task A_wrong_scope_or_executor_cannot_stage_any_initial_bytes(string fault)
    {
        using var f = await GuildFixture.Create(true); var (executor, _) = await BattleExecutorWorkTests.Prepare(f);
        var reader = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, f.Battles);
        var service = new BattleExecutorWorkService(f.Executors, reader, f.Clock, new BattleArtifactStore(f.H.Data.Archive));
        var work = await service.PollAsync(executor, default);
        var before = Directory.GetFiles(f.H.Data.Archive, "*.artifact", SearchOption.AllDirectories).Order().ToArray();
        var chunk = new BattleArtifactChunk { Executor = work.Lease.Clone(), Scope = work.Preparing.Scope.Clone(), TotalBytes = 1,
            Sha256 = ByteString.CopyFrom(SHA256.HashData(new byte[1])), Data = ByteString.CopyFrom(new byte[1]) };
        if (fault == "fence") chunk.Scope.Fence++;
        if (fault == "boundary") chunk.Scope.RestoreBoundary = Guid.NewGuid().ToString("D");
        if (fault == "lease") chunk.Executor.LeaseId = Guid.NewGuid().ToString("D");
        if (fault == "oversize") { chunk.Data = ByteString.CopyFrom(new byte[262145]); chunk.TotalBytes = 262145; }
        var error = await Record.ExceptionAsync(() => service.UploadInitialAsync(chunk, default));
        Assert.True(error is InvalidDataException or OutpostEvidenceUnavailableException, error?.ToString() ?? "unauthorized upload accepted");
        Assert.Empty(Directory.GetFiles(f.H.Data.Archive, "*.part", SearchOption.AllDirectories));
        Assert.Equal(before, Directory.GetFiles(f.H.Data.Archive, "*.artifact", SearchOption.AllDirectories).Order().ToArray());
    }
}
