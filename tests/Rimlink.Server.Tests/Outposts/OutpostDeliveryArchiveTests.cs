using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostDeliveryArchiveTests
{
    [Fact]
    public async Task Fetched_but_not_started_parcel_can_archive()
    {
        using var f = await Fixture.Create(); await f.H.Mailbox.Get(1, f.Request.ParcelId);
        Assert.IsType<ArchiveOutcome.Archived>(await f.Archive());
        Assert.Empty(await f.H.Db.OutpostDeliveryAttempts.AsNoTracking().ToListAsync());
        Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Disconnected_started_delivery_blocks_archive()
    {
        using var f = await Fixture.Create(); await f.H.Parcels.BeginDeliveryAsync(1, f.Request, default);
        var error = await Assert.ThrowsAsync<RpcException>(f.Archive);
        Assert.Equal(ErrorCode.RecoveryRequired, error.ErrorDetail()?.Code);
        Assert.Empty(await f.H.Db.ArchivedColonies.AsNoTracking().ToListAsync());
        Assert.NotEmpty(await f.H.Db.SaveVersions.AsNoTracking().ToListAsync());
        Assert.True((await f.H.Db.OutpostDeliveryAttempts.AsNoTracking().SingleAsync()).Active);
    }

    [Fact]
    public async Task Begin_delivery_vs_archive_has_one_winner()
    {
        using var f = await Fixture.Create();
        var held = await f.H.F.H.H.Authority.AcquireAsync([1], default);
        async Task<bool> Start() { try { await f.H.Parcels.BeginDeliveryAsync(1, f.Request, default); return true; } catch (RpcException) { return false; } }
        async Task<bool> Archive() { try { return await f.Archive() is ArchiveOutcome.Archived; } catch (RpcException) { return false; } }
        var start = Start(); var archive = Archive();
        Assert.False(start.IsCompleted); Assert.False(archive.IsCompleted); await held.DisposeAsync();
        var outcomes = await Task.WhenAll(start, archive); Assert.Single(outcomes, x => x);
        Assert.Equal(1, await f.H.Db.ArchivedColonies.CountAsync() + await f.H.Db.OutpostDeliveryAttempts.CountAsync(a => a.Active));
    }

    private sealed class Fixture : IDisposable
    {
        public MutationHarness H { get; }
        public ServerFixture Host { get; }
        public OutpostDeliveryRequest Request { get; private set; } = null!;
        private Fixture(MutationHarness h)
        {
            H = h; var shared = h.F.H.H;
            Host = HygieneFixtures.HygieneServer().WithoutWorld().WithDataDir(shared.Data.Root).KeepDataDir()
                .WithSetting("Outposts:AnchorDirectory", Path.Combine(shared.Root, "external"))
                .WithSetting("Outposts:StorageContract", "sqlite-local-v1")
                .WithServices(s => { s.AddSingleton(shared.Files); s.AddSingleton(shared.Owners); s.AddSingleton(shared.Anchor); });
        }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(await MutationHarness.Create());
            var close = await f.H.Service.MutateAsync(1, f.H.Request(OutpostMutationKind.Close), default);
            long id = Assert.Single(close.ParcelIds);
            var link = await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == id);
            f.Request = new() { Scope = f.H.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"), ParcelId = id,
                PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(f.H.Post) };
            f.H.Db.WorldObjects.Add(new Rimlink.Server.Storage.Entities.WorldObject { OwnerPlayerId = 1, Type = WorldObjectTypes.Settlement, Name = "Nova Home", Tile = 33, CreatedAt = DateTime.UtcNow });
            await f.H.Db.SaveChangesAsync(); _ = f.Host.Services;
            return f;
        }
        public async Task<ArchiveOutcome> Archive()
        {
            using var scope = Host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(1, "admin", Actor.System);
        }
        public void Dispose() { Host.Dispose(); H.Dispose(); }
    }
}
