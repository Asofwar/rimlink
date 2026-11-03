using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Tests.Outposts;

public class OutpostManifestTests
{
    [Fact]
    public async Task No_foundation_before_admin_confirmation()
    {
        using var f = await ManifestFixture.Create();
        var preview = await f.Service.StageAsync(Actor.Api, await f.Document(), default);
        await using (var lease = await f.H.Authority.AcquireAsync([], default))
            await Assert.ThrowsAsync<RpcException>(() => f.Service.RequireActiveHeldAsync(lease, "planet", 0, OutpostProfile.Wood, default));
        await f.Service.ConfirmAsync(Actor.Api, preview.ManifestId, preview.Digest, default);
        await using (var ready = await f.H.Authority.AcquireAsync([], default))
        {
            var active = await f.Service.RequireActiveHeldAsync(ready, "planet", 0, OutpostProfile.Wood, default);
            Assert.Equal(4, active.TileCount);
        }
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Same_seed_different_parameters_or_defs_is_not_same_manifest()
    {
        using var f = await ManifestFixture.Create();
        var document = await f.Document();
        var world = await f.H.Db.World.SingleAsync();
        world.Params = new WorldParams { Seed = "same", Rainfall = 2 }.ToByteArray();
        await f.H.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<RpcException>(() => f.Service.StageAsync(Actor.Api, document, default));
        var fresh = await f.Document();
        var preview = await f.Service.StageAsync(Actor.Api, fresh, default);
        f.H.Db.ModpackDefs.Add(new ModpackDef { DefName = "NewBiome" });
        await f.H.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<RpcException>(() => f.Service.ConfirmAsync(Actor.Api, preview.ManifestId, preview.Digest, default));
        Assert.False((await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync()).Confirmed);
    }

    [Theory]
    [InlineData(0, OutpostProfile.Wood, true)]
    [InlineData(1, OutpostProfile.Wood, false)]
    [InlineData(1, OutpostProfile.Steel, true)]
    [InlineData(2, OutpostProfile.Steel, false)]
    [InlineData(3, OutpostProfile.Steel, false)]
    public async Task Wood_requires_natural_trees_steel_only_accessible_land(int tile, OutpostProfile profile, bool allowed)
    {
        using var f = await ManifestFixture.Create();
        var preview = await f.Service.StageAsync(Actor.Api, await f.Document(), default);
        await f.Service.ConfirmAsync(Actor.Api, preview.ManifestId, preview.Digest, default);
        await using var lease = await f.H.Authority.AcquireAsync([], default);
        if (allowed) Assert.NotNull(await f.Service.RequireActiveHeldAsync(lease, "planet", tile, profile, default));
        else await Assert.ThrowsAsync<RpcException>(() => f.Service.RequireActiveHeldAsync(lease, "planet", tile, profile, default));
    }

    [Theory]
    [InlineData("duplicate")][InlineData("missing")][InlineData("surface")][InlineData("biome")]
    [InlineData("schema")][InlineData("world")][InlineData("defs")][InlineData("valuation")][InlineData("range")]
    public async Task Duplicate_missing_unknown_surface_tiles_rejected(string defect)
    {
        using var f = await ManifestFixture.Create();
        var doc = await f.Document();
        switch (defect)
        {
            case "duplicate": doc.Tiles[1].Tile = 0; break;
            case "missing": doc.Tiles.RemoveAt(1); break;
            case "surface": doc.Tiles[1].SurfaceId = "orbit"; break;
            case "biome": doc.Tiles[1].BiomeDef = "UnknownBiome"; break;
            case "schema": doc.SchemaVersion = 2; break;
            case "world": doc.Binding.WorldInstanceId = "other-world"; break;
            case "defs": doc.GameDefsSha256 = ByteString.Empty; break;
            case "valuation": doc.Resources[0].UnitValueX100 = -1; break;
            case "range": doc.TileCount = 2_000_001; break;
        }
        await Assert.ThrowsAsync<RpcException>(() => f.Service.StageAsync(Actor.Api, doc, default));
        Assert.Empty(await f.H.Db.OutpostManifests.ToListAsync());
    }

    [Fact]
    public async Task Non_admin_and_wrong_confirmation_digest_have_no_effect()
    {
        using var f = await ManifestFixture.Create();
        var user = new Actor(1, 1, Role.Player, "user");
        var doc = await f.Document();
        await Assert.ThrowsAsync<RpcException>(() => f.Service.StageAsync(user, doc, default));
        var p = await f.Service.StageAsync(Actor.Api, doc, default);
        await Assert.ThrowsAsync<RpcException>(() => f.Service.ConfirmAsync(user, p.ManifestId, p.Digest, default));
        await Assert.ThrowsAsync<RpcException>(() => f.Service.ConfirmAsync(Actor.Api, p.ManifestId, new string('0', 64), default));
        Assert.False((await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync()).Active);
    }

    [Fact]
    public async Task Preview_bytes_are_immutable_and_reviewable_with_digest_bound_pages()
    {
        using var f = await ManifestFixture.Create();
        var doc = await f.Document();
        byte[] original = doc.ToByteArray();
        var p = await f.Service.StageAsync(Actor.Api, doc, default);
        doc.Tiles[0].NaturalTrees = false;
        var page = await f.Service.ReadAsync(Actor.Api, new() { ManifestId = p.ManifestId, ExpectedDigest = p.Digest }, default);
        Assert.Equal(original, page.Data.ToByteArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), p.Digest);
        var same = await f.Service.StageAsync(Actor.Api, OutpostManifestDocument.Parser.ParseFrom(original), default);
        Assert.Equal(p.ManifestId, same.ManifestId);
        await f.Service.ConfirmAsync(Actor.Api, p.ManifestId, p.Digest, default);
        await f.Service.ConfirmAsync(Actor.Api, p.ManifestId, p.Digest, default);
        Assert.Single(await f.H.Db.OutpostManifests.ToListAsync());
    }

    [Fact]
    public async Task Manifest_replacement_does_not_relocate_or_delete_outpost()
    {
        using var f = await ManifestFixture.Create();
        var old = await f.Service.StageAsync(Actor.Api, await f.Document(), default);
        await f.Service.ConfirmAsync(Actor.Api, old.ManifestId, old.Digest, default);
        await using (var lease = await f.H.Authority.AcquireAsync([1], default))
        {
            var head = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
            await f.H.Authority.CommitHeldAsync(lease, new(Guid.NewGuid().ToString("D"), "world-1", 1, 0, head.RestoreBoundary,
                "Production", "fixture", new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.Empty), (db, ct) =>
            {
                db.Outposts.Add(new OutpostRow { Id = "kept", WorldId = "world-1", PlayerId = 1, Tile = 0,
                    Profile = (int)OutpostProfile.Wood, State = "Active", OccupiesSlot = true, Stock = 19,
                    FractionNumerator = "1", FractionDenominator = "2", ManifestId = old.ManifestId,
                    ManifestDigest = Convert.FromHexString(old.Digest), CheckpointUtc = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc),
                    ClockProcessId = "old", ClockFrequency = TimeSpan.TicksPerSecond });
                return Task.CompletedTask;
            }, default);
        }
        var replacement = await f.Document();
        replacement.Tiles[0].NaturalTrees = false;
        var next = await f.Service.StageAsync(Actor.Api, replacement, default);
        await f.Service.ConfirmAsync(Actor.Api, next.ManifestId, next.Digest, default);
        var kept = await f.H.Db.Outposts.AsNoTracking().SingleAsync();
        Assert.Equal(0, kept.Tile); Assert.Equal(19, kept.Stock);
        Assert.Equal("1", kept.FractionNumerator); Assert.Equal("2", kept.FractionDenominator);
        Assert.Equal("SafetyHold", kept.State); Assert.Equal(old.ManifestId, kept.ManifestId);
        Assert.Equal(new DateTime(2026, 11, 3), kept.CheckpointUtc);
        Assert.True(kept.OccupiesSlot);
        Assert.Equal(2, await f.H.Db.OutpostManifests.CountAsync());
    }
}

internal sealed class ManifestFixture : IDisposable
{
    public AuthorityHarness H { get; } = new();
    public OutpostManifestService Service { get; }
    private ManifestFixture() => Service = new(H.Db, H.Authority, new OutpostClock(TimeProvider.System), H.Files);
    public static async Task<ManifestFixture> Create()
    {
        var f = new ManifestFixture();
        f.H.Anchor.Provision();
        f.H.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1", Seed = "same", Params = new WorldParams { Seed = "same", Rainfall = 1 }.ToByteArray() });
        f.H.Db.ModpackDefs.AddRange(new[] { "TemperateForest", "Desert", "Ocean", "WoodLog", "Steel" }.Select(n => new ModpackDef { DefName = n }));
        await f.H.Db.SaveChangesAsync();
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.InitializeAsync(new("world-1", "fixture", true), default)).State);
        return f;
    }
    public async Task<OutpostManifestDocument> Document()
    {
        var d = new OutpostManifestDocument { SchemaVersion = 1, Binding = await Service.BindingAsync(Actor.Api, default),
            GameVersion = "1.7", GameDefsSha256 = ByteString.CopyFrom(new byte[32]), MainSurfaceId = "planet", TileCount = 4, ValuationVersion = "v1" };
        d.Tiles.Add(new OutpostManifestTile { Tile = 0, SurfaceId = "planet", BiomeDef = "TemperateForest", Land = true, CaravanPassable = true, NaturalTrees = true });
        d.Tiles.Add(new OutpostManifestTile { Tile = 1, SurfaceId = "planet", BiomeDef = "Desert", Land = true, CaravanPassable = true });
        d.Tiles.Add(new OutpostManifestTile { Tile = 2, SurfaceId = "planet", BiomeDef = "Ocean", CaravanPassable = true });
        d.Tiles.Add(new OutpostManifestTile { Tile = 3, SurfaceId = "planet", BiomeDef = "Desert", Land = true });
        d.Resources.Add(new OutpostResourceValuation { DefName = "Steel", UnitValueX100 = 190 });
        d.Resources.Add(new OutpostResourceValuation { DefName = "WoodLog", UnitValueX100 = 120 });
        return d;
    }
    public void Dispose() => H.Dispose();
}
