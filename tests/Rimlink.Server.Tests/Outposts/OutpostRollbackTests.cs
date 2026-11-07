using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostRollbackTests
{
    [Fact]
    public async Task Rollback_uses_IsCurrent_and_advances_current_epoch_once_without_rolling_back_stock()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
        var target = f.H.Post;
        var newer = f.H.F.H.Snapshot(target.Recovery.Outposts.Metadata.Clone(), tick: 2000);
        await f.H.F.H.Accept(newer);
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(20, Assert.Single((await f.H.Service.StateAsync(1, new() { Scope = f.H.Scope.Clone() }, default)).Outposts).Stock);
        string original = await Historical(f);
        string oldHead = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        using (var services = f.Host.Services.CreateScope())
            Assert.True(await services.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, target.Version.Number));
        Assert.Equal(target.Version.Id, (await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent)).Id);
        Assert.Equal(1, (await f.H.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).SaveWriteEpoch);
        Assert.Equal(0, (await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.Id == target.Version.Id)).SaveWriteEpoch);
        Assert.Equal(20, (await f.H.Row()).Stock);
        Assert.Equal(original, await Historical(f));
        Assert.NotEqual(oldHead, (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        var operation = Assert.Single(await f.H.Db.OutpostLifecycleOperations.AsNoTracking().ToListAsync());
        Assert.Equal("Rollback", operation.Kind); Assert.Equal("Completed", operation.Phase);
        using (var services = f.Host.Services.CreateScope())
            Assert.True(await services.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, target.Version.Number));
        Assert.Equal(1, (await f.H.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).SaveWriteEpoch);
        Assert.Single(await f.H.Db.OutpostLifecycleOperations.AsNoTracking().ToListAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("before-removal")]
    [InlineData("missing-bytes")]
    [InlineData("changed-scope")]
    public async Task Incompatible_rollback_refuses_before_any_database_or_file_effect(string fault)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
        var target = fault == "before-removal" ? f.H.F.Pre : f.H.Post;
        if (fault == "missing-bytes") File.Delete(f.H.F.H.H.Files.FullPath(1, target.Version.Number));
        if (fault == "changed-scope")
            await f.H.Db.SaveVersions.Where(v => v.Id == target.Version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SaveWriteEpoch, 9L));
        string before = await f.Cut();
        using var services = f.Host.Services.CreateScope();
        await Assert.ThrowsAsync<RpcException>(() => services.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, target.Version.Number));
        Assert.Equal(before, await f.Cut());
    }

    [Theory]
    [InlineData("BeforePending", false)]
    [InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)]
    [InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_rollback_reopens_only_exact_old_or_next_epoch_and_retry_does_not_advance_twice(string checkpoint, bool committed)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
        var target = f.H.Post;
        var newer = f.H.F.H.Snapshot(target.Recovery.Outposts.Metadata.Clone(), tick: 2000); await f.H.F.H.Accept(newer);
        using (var services = f.Host.Services.CreateScope())
        {
            services.ServiceProvider.GetRequiredService<OutpostAuthority>().CommitCheckpoint = at =>
            { if (at == checkpoint) throw new IOException("simulated rollback interruption: " + checkpoint); };
            await Assert.ThrowsAsync<IOException>(() => services.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, target.Version.Number));
        }
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        Assert.Equal(committed ? 1 : 0, (await f.H.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).SaveWriteEpoch);
        Assert.Equal(committed ? target.Version.Id : newer.Version.Id,
            (await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent)).Id);
        using (var services = f.Host.Services.CreateScope())
            Assert.True(await services.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, target.Version.Number));
        Assert.Equal(1, (await f.H.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).SaveWriteEpoch);
        Assert.Single(await f.H.Db.OutpostLifecycleOperations.AsNoTracking().ToListAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Unrelated_legacy_owner_keeps_081_rollback_with_another_owners_protected_history(bool includedInCleanup)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
        var versions = await LegacyVersions(f);
        if (includedInCleanup)
        {
            await OutpostLifecycleTests.CloseAndReceive(f);
            using var reset = f.Host.Services.CreateScope();
            await reset.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Admin.AdminOperations>()
                .ResetPlayer(Rimlink.Server.Application.Admin.Actor.Api, 1);
        }
        string originalHistory = await Historical(f);
        using var services = f.Host.Services.CreateScope(); var store = services.ServiceProvider.GetRequiredService<SaveStore>();
        Assert.True(await store.Rollback(2, 1));
        Assert.Equal(versions[0].Id, (await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 2 && v.IsCurrent)).Id);
        Assert.Equal(1, await f.H.Db.Players.Where(p => p.Id == 2).Select(p => p.SaveWriteEpoch).SingleAsync());
        Assert.Equal(originalHistory, await Historical(f));
        var opened = (await store.OpenCurrent(2))!.Value;
        await using (opened.Content)
        {
            Assert.Null(opened.RollbackBridge); Assert.Equal(1, opened.WriteEpoch);
            using var raw = new ZstdSharp.DecompressionStream(opened.Content);
            var recovered = Rimlink.Protocol.SaveRecoveryData.ReadXml(raw, out var hash);
            Assert.Equal(versions[0].SnapshotId, recovered.SnapshotId); Assert.Equal(versions[0].RawSha256, hash); Assert.False(recovered.Outposts.Present);
        }
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Legacy_rollback_pending_reconciles_once(string checkpoint, bool committed)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); var versions = await LegacyVersions(f); string history = await Historical(f);
        using (var services = f.Host.Services.CreateScope())
        {
            services.ServiceProvider.GetRequiredService<OutpostAuthority>().CommitCheckpoint = at =>
            { if (at == checkpoint) throw new IOException("legacy rollback interruption: " + at); };
            await Assert.ThrowsAsync<IOException>(() => services.ServiceProvider.GetRequiredService<SaveStore>().Rollback(2, 1));
        }
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        Assert.Equal(committed ? 1 : 0, await f.H.Db.Players.Where(p => p.Id == 2).Select(p => p.SaveWriteEpoch).SingleAsync());
        Assert.Equal(versions[committed ? 0 : 1].Id, (await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 2 && v.IsCurrent)).Id);
        using (var services = f.Host.Services.CreateScope()) Assert.True(await services.ServiceProvider.GetRequiredService<SaveStore>().Rollback(2, 1));
        Assert.Equal(1, await f.H.Db.Players.Where(p => p.Id == 2).Select(p => p.SaveWriteEpoch).SingleAsync());
        Assert.Single(await f.H.Db.OutpostLifecycleOperations.AsNoTracking().ToListAsync()); Assert.Equal(history, await Historical(f));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Fact]
    public async Task Legacy_rollback_accepts_a_real_fresh_legacy_upload_without_synthetic_outpost_evidence()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); _ = await LegacyVersions(f);
        using var services = f.Host.Services.CreateScope(); var store = services.ServiceProvider.GetRequiredService<SaveStore>();
        Assert.True(await store.Rollback(2, 1));
        byte[] packed = Support.RecoveryFixtures.Save(3000, epoch: 1);
        var xml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(Support.SaveFixtures.Decompress(packed)));
        xml.Root!.Element("game")!.Element("world")!.Element("info")!.Element("seedString")!.Value = (await f.H.Db.World.AsNoTracking().SingleAsync()).Seed;
        packed = Support.SaveFixtures.Compress(System.Text.Encoding.UTF8.GetBytes(xml.ToString()));
        var header = Support.RecoveryFixtures.Header(packed, 3000);
        var version = await store.Store(2, header, LegacyChunks(packed), default);
        Assert.Equal(1, version.SaveWriteEpoch); Assert.Equal(header.SnapshotId, version.SnapshotId);
        Assert.Empty(await f.H.Db.OutpostSnapshotLinks.Where(l => l.PlayerId == 2).ToListAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        var downloaded = (await store.OpenCurrent(2))!.Value; await using (downloaded.Content) { Assert.Null(downloaded.RollbackBridge); Assert.Equal(version.SnapshotId, downloaded.Version.SnapshotId); }
    }

    [Fact]
    public async Task Legacy_rollback_download_refuses_a_changed_owner_projection_before_returning_bytes()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); _ = await LegacyVersions(f);
        using var services = f.Host.Services.CreateScope(); var store = services.ServiceProvider.GetRequiredService<SaveStore>();
        Assert.True(await store.Rollback(2, 1));
        await f.H.Db.Players.Where(p => p.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveWriteEpoch, 9L));
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            var unexpected = await store.OpenCurrent(2);
            if (unexpected is { } opened) await opened.Content.DisposeAsync();
        });
        Assert.Equal(ErrorCode.RecoveryRequired, error.ErrorDetail()?.Code);
        Assert.Equal("protected download authority is not ready", error.Status.Detail);
    }

    [Theory]
    [InlineData("corrupt-bytes")][InlineData("tagged-missing-link")][InlineData("wrong-generation")]
    public async Task Protected_or_unknown_target_never_falls_back_to_legacy(string fault)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); var versions = await LegacyVersions(f); var target = versions[0];
        string path = f.H.F.H.H.Files.FullPath(2, 1);
        if (fault == "corrupt-bytes") await File.WriteAllBytesAsync(path, [1, 2, 3]);
        else if (fault == "wrong-generation") await f.H.Db.SaveVersions.Where(v => v.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SaveGeneration, 9L));
        else
        {
            var scope = f.H.Scope.Clone(); scope.OwnerPlayerId = 2;
            byte[] packed = Support.RecoveryFixtures.Save(1000, target.SnapshotId, outposts: new() { Metadata = new() { SchemaVersion = 1, Scope = scope } });
            var recovery = Rimlink.Protocol.SaveRecoveryData.ReadXml(new MemoryStream(Support.SaveFixtures.Decompress(packed)), out byte[] hash);
            f.H.F.H.H.Files.WriteAtomically(path, packed);
            target.Size = packed.Length; target.Sha256 = System.Security.Cryptography.SHA256.HashData(packed); target.RawSha256 = hash; target.RecoveryJson = JsonSerializer.Serialize(recovery);
            await f.H.Db.SaveChangesAsync();
        }
        string before = await f.Cut(); using var services = f.Host.Services.CreateScope();
        await Assert.ThrowsAsync<RpcException>(() => services.ServiceProvider.GetRequiredService<SaveStore>().Rollback(2, 1));
        Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Legacy_rollback_keeps_exact_retained_bytes_independent_of_live_version_files()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); _ = await LegacyVersions(f);
        using var services = f.Host.Services.CreateScope(); Assert.True(await services.ServiceProvider.GetRequiredService<SaveStore>().Rollback(2, 1));
        var operation = await f.H.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync(); var intent = OutpostLifecycleEvidence.Intent(operation);
        Assert.Equal("LegacySnapshot", intent.Rollback!.Kind); Assert.Empty(intent.Rollback.Target); Assert.Empty(intent.Rollback.PreviousCurrent);
        Assert.Empty(await f.H.Db.OutpostSnapshotLinks.Where(l => l.PlayerId == 2).ToListAsync());
        var codec = new OutpostLifecycleEvidence(f.H.Db, f.H.F.H.H.Files);
        await codec.ValidateStoredAsync(default);
        string path = Path.Combine(f.H.F.H.H.Files.EvidenceArchiveDirectory(intent.Directory), "2", intent.Files[0].Name);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }

    private static async Task<List<Rimlink.Server.Storage.Entities.SaveVersion>> LegacyVersions(LifecycleFixture f)
    {
        f.H.Db.Players.Add(new Rimlink.Server.Storage.Entities.Player { Id = 2, Name = "Legacy", RecoveryVersion = 1, Account = new() { Login = "legacy-rollback" } });
        await f.H.Db.SaveChangesAsync();
        var versions = new List<Rimlink.Server.Storage.Entities.SaveVersion>();
        // Real 0.8.1 XML/packed bytes and ordinary version metadata; deliberately no outpost section/link for owner 2.
        for (int number = 1; number <= 2; number++)
        {
            string id = Guid.NewGuid().ToString("N");
            byte[] packed = Support.RecoveryFixtures.Save(number * 1000, id);
            var recovery = Rimlink.Protocol.SaveRecoveryData.ReadXml(new MemoryStream(Support.SaveFixtures.Decompress(packed)), out var rawHash);
            var version = new Rimlink.Server.Storage.Entities.SaveVersion { PlayerId = 2, Number = number, GameTick = number * 1000,
                SnapshotId = id, Size = packed.Length, Sha256 = System.Security.Cryptography.SHA256.HashData(packed), RawSha256 = rawHash,
                RecoveryVersion = recovery.Version, RecoveryJson = JsonSerializer.Serialize(recovery), IsCurrent = number == 2, CreatedAt = DateTime.UtcNow };
            await using (var lease = await f.H.F.H.H.Authority.AcquireAsync([2], default))
            {
                f.H.F.H.H.Files.WriteAtomically(f.H.F.H.H.Files.FullPath(2, number), packed);
                f.H.Db.SaveVersions.Add(version); await f.H.Db.SaveChangesAsync();
            }
            versions.Add(version);
        }
        return versions;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> LegacyChunks(byte[] packed) { yield return packed; await Task.CompletedTask; }

    private static async Task<string> Historical(LifecycleFixture f) => JsonSerializer.Serialize(new
    {
        Links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(v => v.SnapshotId).ToListAsync(),
        Custody = await f.H.Db.PawnCustody.AsNoTracking().OrderBy(v => v.Id).ToListAsync()
    });
}
