using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using OutpostAuthority = Rimlink.Server.Application.Outposts.OutpostAuthority;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostSnapshotTests
{
    [Fact]
    public async Task Accepted_upload_binds_real_bytes_and_registry_in_one_protected_commit()
    {
        using var f = await SnapshotServer.Create();
        var metadata = await f.Metadata();
        metadata.Registry.Add(new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = SnapshotHarness.Custody,
            Scope = metadata.Scope.Clone(), PawnReferenceToken = "Thing_Human1" });
        var info = await f.Upload(metadata);
        using var scope = f.Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(info.SnapshotId, Assert.Single(db.OutpostSnapshotLinks.AsNoTracking()).SnapshotId);
        Assert.Equal("Home", Assert.Single(db.PawnCustody.AsNoTracking()).LocationKind);
        Assert.Equal(OutpostStartupState.Ready, (await scope.ServiceProvider.GetRequiredService<OutpostAuthority>().OpenAsync(default)).State);
    }

    [Fact]
    public async Task Protected_history_rejects_upload_without_section()
    {
        using var f = await SnapshotServer.Create();
        await f.Upload(await f.Metadata());
        var bytes = RecoveryFixtures.Save(2000);
        var error = await Assert.ThrowsAsync<RpcException>(() => SaveFixtures.Upload(new Protocol.V1.Saves.SavesClient(f.Server.CreateChannel()),
            f.Player.SessionToken, bytes, RecoveryFixtures.Header(bytes, 2000)));
        Assert.Equal(ErrorCode.RecoveryRequired, error.ErrorDetail()?.Code);
        using var scope = f.Server.Services.CreateScope();
        Assert.Single(scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().SaveVersions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Referenced_delta_and_archive_evidence_survive_retention(bool delta)
    {
        using var f = await SnapshotServer.Create();
        var first = await f.Upload(await f.Metadata());
        if (delta)
        {
            using var scope = f.Server.Services.CreateScope();
            var files = scope.ServiceProvider.GetRequiredService<SaveFiles>();
            using var held = await files.PerStoreLock.HoldAsync();
            byte[] packed = File.ReadAllBytes(files.FullPath(f.Player.Player.PlayerId, first.Version));
            byte[] raw = SaveFixtures.Decompress(packed);
            files.WriteAtomically(files.BasePath(f.Player.Player.PlayerId, first.Version), packed);
            files.WriteAtomically(files.DeltaPath(f.Player.Player.PlayerId, first.Version, first.Version), SaveDelta.Compress(raw, raw));
            File.Delete(files.FullPath(f.Player.Player.PlayerId, first.Version));
        }
        for (int i = 2; i <= 14; i++) await f.Upload(await f.Metadata(), i * 1000);
        f.Restart();
        using var check = f.Server.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(14, await db.OutpostSnapshotLinks.CountAsync());
        Assert.Equal(14, await db.SaveVersions.CountAsync());
        Assert.Equal(OutpostStartupState.Ready, (await check.ServiceProvider.GetRequiredService<OutpostAuthority>().OpenAsync(default)).State);
        var filesAfter = check.ServiceProvider.GetRequiredService<SaveFiles>();
        if (delta) Assert.True(File.Exists(filesAfter.DeltaPath(f.Player.Player.PlayerId, first.Version, first.Version)));
        else Assert.True(File.Exists(filesAfter.FullPath(f.Player.Player.PlayerId, first.Version)));
    }

    [Fact]
    public async Task Failed_protected_commit_never_publishes_row_or_custody()
    {
        using var f = await SnapshotServer.Create();
        var metadata = await f.Metadata();
        metadata.Registry.Add(new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = SnapshotHarness.Custody,
            Scope = metadata.Scope.Clone(), PawnReferenceToken = "Thing_Human1" });
        using var scope = f.Server.Services.CreateScope();
        var authority = scope.ServiceProvider.GetRequiredService<OutpostAuthority>();
        authority.CommitCheckpoint = point => { if (point == "BeforePending") throw new IOException("owned-storage-stop"); };
        byte[] bytes = RecoveryFixtures.Save(1000, outposts: new() { Metadata = metadata });
        async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks() { yield return bytes; await Task.CompletedTask; }
        await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<SaveStore>().Store(f.Player.Player.PlayerId,
            RecoveryFixtures.Header(bytes, 1000), Chunks(), default));
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Empty(db.OutpostSnapshotLinks); Assert.Empty(db.PawnCustody); Assert.Empty(db.SaveVersions);
    }
}

internal sealed class SnapshotServer : IDisposable
{
    private readonly AuthorityHarness anchor = new();
    public ServerFixture Server { get; private set; }
    public LoginReply Player { get; private set; } = null!;
    private SnapshotServer() => Server = HygieneFixtures.HygieneServer()
        .WithSetting("Outposts:AnchorDirectory", Path.Combine(anchor.Root, "external"))
        .WithSetting("Outposts:StorageContract", "sqlite-local-v1").WithOutpostUnitQualification();
    public static async Task<SnapshotServer> Create()
    {
        var f = new SnapshotServer();
        using (var scope = f.Server.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<OutpostAnchorStore>().Provision();
            string world = await sp.GetRequiredService<RimlinkDbContext>().World.Select(w => w.InfluenceInstanceId).SingleAsync();
            Assert.Equal(OutpostStartupState.Ready, (await sp.GetRequiredService<OutpostAuthority>().InitializeAsync(new(world, "fixture", true), default)).State);
            await OutpostUnitComposition.Activate(sp);
        }
        f.Player = await f.Server.RegisterAndLogin("nova", playerName: "Nova");
        return f;
    }
    public async Task<OutpostSaveMetadata> Metadata()
    {
        using var scope = Server.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().OutpostAuthority.AsNoTracking().SingleAsync();
        return new() { SchemaVersion = 1, Scope = new() { WorldInstanceId = row.WorldId, OwnerPlayerId = Player.Player.PlayerId,
            RestoreBoundary = row.RestoreBoundary } };
    }
    public Task<SaveInfo> Upload(OutpostSaveMetadata metadata, long tick = 1000)
    {
        byte[] bytes = RecoveryFixtures.Save(tick, outposts: new() { Metadata = metadata });
        return SaveFixtures.Upload(new Protocol.V1.Saves.SavesClient(Server.CreateChannel()), Player.SessionToken, bytes, RecoveryFixtures.Header(bytes, tick));
    }
    public void Restart()
    {
        string directory = Server.DataDir;
        Server.KeepDataDir().Dispose();
        Server = HygieneFixtures.HygieneServer().WithoutWorld().WithDataDir(directory)
            .WithSetting("Outposts:AnchorDirectory", Path.Combine(anchor.Root, "external"))
            .WithSetting("Outposts:StorageContract", "sqlite-local-v1").WithOutpostUnitQualification();
    }
    public void Dispose() { Server.Dispose(); anchor.Dispose(); }
}
