using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.HygieneFixtures;
using WorldClient = Rimlink.Protocol.V1.World.WorldClient;
using OutpostAuthority = Rimlink.Server.Application.Outposts.OutpostAuthority;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostTileTests
{
    [Theory]
    [InlineData("Active")]
    [InlineData("SafetyHold")]
    [InlineData("Archived")]
    public async Task Retained_outpost_blocks_its_tile_but_not_adjacent_tile(string state)
    {
        using var h = new AuthorityHarness();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "tile-world" });
        h.Db.Outposts.Add(new OutpostRow { Id = "occupied", WorldId = "tile-world", PlayerId = 1, Tile = 10, State = state, OccupiesSlot = true });
        await h.Db.SaveChangesAsync();
        var claims = new OutpostTileClaims(h.Db, h.Authority);
        await using var lease = await h.Authority.AcquireAsync([1], default);
        var refusal = await Assert.ThrowsAsync<RpcException>(() => claims.RequireAvailableHeldAsync(lease, "planet", 10, null, default));
        Assert.Equal(StatusCode.FailedPrecondition, refusal.StatusCode);
        await claims.RequireAvailableHeldAsync(lease, "planet", 11, null, default);
    }

    [Fact]
    public async Task Only_exact_active_reservation_can_be_reused()
    {
        using var h = new AuthorityHarness();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "tile-world", NpcTiles = "22" });
        h.Db.OutpostReservations.Add(new OutpostReservation { Id = "reservation", WorldId = "tile-world", PlayerId = 1, Tile = 10, Active = true });
        await h.Db.SaveChangesAsync();
        var claims = new OutpostTileClaims(h.Db, h.Authority);
        await using var lease = await h.Authority.AcquireAsync([1], default);
        await claims.RequireAvailableHeldAsync(lease, "planet", 10, "reservation", default);
        await Assert.ThrowsAsync<RpcException>(() => claims.RequireAvailableHeldAsync(lease, "planet", 10, "other", default));
        await Assert.ThrowsAsync<RpcException>(() => claims.RequireAvailableHeldAsync(lease, "planet", 22, null, default));
        await Assert.ThrowsAsync<RpcException>(() => claims.RequireAvailableHeldAsync(lease, "orbit", 11, null, default));
    }

    [Fact]
    public async Task Settle_respects_prepared_outpost_reservation()
    {
        await using var f = HygieneServer();
        var player = await f.RegisterAndLogin("reserved");
        await Reserve(f, 10);
        var world = new WorldClient(f.CreateChannel());
        var ex = await Assert.ThrowsAsync<RpcException>(() => world.SettleAsync(new SettleRequest { Tile = 10, Name = "Home" }, Bearer(player.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal(0, await Db(f, db => db.WorldObjects.CountAsync()));
        await world.SettleAsync(new SettleRequest { Tile = 11, Name = "Home" }, Bearer(player.SessionToken));
    }

    [Fact]
    public async Task Archived_settlement_restore_respects_outpost()
    {
        await using var f = HygieneServer();
        var colony = await Settled(f, "archived", 10);
        f.Services.GetRequiredService<SessionRegistry>().End(colony.Token);
        long archive;
        using (var scope = f.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(colony.PlayerId, "admin", Rimlink.Server.Application.Admin.Actor.Api);
            Assert.IsType<ArchiveOutcome.Archived>(result);
            archive = await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().ArchivedColonies.Select(c => c.Id).SingleAsync();
        }
        await Reserve(f, 10);
        using (var scope = f.Services.CreateScope())
        {
            Assert.IsType<RestoreOutcome.TileTaken>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archive, "test"));
        }
        Assert.Equal(0, await Db(f, db => db.WorldObjects.CountAsync()));
        Assert.Equal(ArchiveState.Archived, await Db(f, db => db.ArchivedColonies.Select(a => a.State).SingleAsync()));
    }

    [Fact]
    public async Task Settle_and_found_same_tile_have_one_winner()
    {
        using var anchor = new AuthorityHarness();
        await using var f = HygieneServer().WithSetting("Outposts:AnchorDirectory", Path.Combine(anchor.Root, "external"))
            .WithSetting("Outposts:StorageContract", "sqlite-local-v1");
        var player = await f.RegisterAndLogin("racing");
        var files = f.Services.GetRequiredService<SaveFiles>();
        var held = await files.PerStoreLock.HoldAsync();
        var settle = Settle();
        var found = Found();
        held.Dispose();
        var results = await Task.WhenAll(settle, found);
        Assert.Single(results, won => won);
        Assert.Equal(1, await Db(f, async db => await db.WorldObjects.CountAsync(o => o.Tile == 10) + await db.OutpostReservations.CountAsync(r => r.Tile == 10 && r.Active)));
        async Task<bool> Settle()
        {
            try { await new WorldClient(f.CreateChannel()).SettleAsync(new SettleRequest { Tile = 10, Name = "Home" }, Bearer(player.SessionToken)); return true; }
            catch (RpcException e) when (e.StatusCode == StatusCode.FailedPrecondition) { return false; }
        }
        async Task<bool> Found()
        {
            using var scope = f.Services.CreateScope();
            await using var lease = await scope.ServiceProvider.GetRequiredService<OutpostAuthority>().AcquireAsync([player.Player.PlayerId], default);
            try
            {
                await scope.ServiceProvider.GetRequiredService<OutpostTileClaims>().RequireAvailableHeldAsync(lease, "planet", 10, null, default);
                await ReserveHeld(scope.ServiceProvider.GetRequiredService<RimlinkDbContext>(), 10);
                return true;
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.FailedPrecondition) { return false; }
        }
    }

    private static async Task Reserve(ServerFixture f, int tile)
    {
        using var scope = f.Services.CreateScope();
        await ReserveHeld(scope.ServiceProvider.GetRequiredService<RimlinkDbContext>(), tile);
    }
    private static async Task ReserveHeld(RimlinkDbContext db, int tile)
    {
        string world = await db.World.Select(w => w.InfluenceInstanceId).SingleAsync();
        db.OutpostReservations.Add(new OutpostReservation { Id = Guid.NewGuid().ToString("D"), WorldId = world, Tile = tile, Active = true });
        await db.SaveChangesAsync();
    }
}
