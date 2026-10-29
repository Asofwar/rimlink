using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using WorldEntity = Rimlink.Server.Storage.Entities.World;
using Rimlink.Server.Application.Feed;

namespace Rimlink.Server.Application.World;

public sealed class WorldService(RimlinkDbContext db, EventBus events, AdminFeed feed, IOptions<ServerOptions> server, IOptions<HygieneOptions> hygiene, ILogger<WorldService> log)
{
    private const int SqliteConstraint = 19;

    public Task<WorldEntity?> Get() => db.World.AsNoTracking().FirstOrDefaultAsync();

    public Task<bool> Exists() => db.World.AnyAsync();

    public async Task<WorldEntity> Create(WorldParams parameters, IReadOnlyList<int> npcTiles, Session actor)
    {
        if (actor.Role != Storage.Entities.Role.Admin) throw RpcErrors.With(StatusCode.PermissionDenied, ErrorCode.Unspecified, RefusalKeys.WorldOnlyAdminCreates, "only an admin can create the world");
        if (await db.World.AnyAsync()) throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.WorldAlreadyExists, "the world already exists; recreate it through the admin tools");
        if (string.IsNullOrWhiteSpace(parameters.Seed)) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.WorldEmptySeed, "seed is empty");

        var world = new WorldEntity
        {
            Seed = parameters.Seed,
            Params = parameters.ToByteArray(),
            NpcTiles = string.Join(',', npcTiles.Distinct().Order()),
            CreatedAt = DateTime.UtcNow,
        };
        db.World.Add(world);
        await db.SaveChangesAsync();
        log.LogInformation("World created by {Player}: seed {Seed}, {NpcTiles} NPC tiles", actor.PlayerName, world.Seed, npcTiles.Count);
        return world;
    }

    public static WorldInfo ToInfo(WorldEntity? world)
    {
        if (world is null) return new WorldInfo { Exists = false };
        var info = new WorldInfo { Exists = true, Params = WorldParams.Parser.ParseFrom(world.Params) };
        info.NpcTiles.AddRange(ParseTiles(world.NpcTiles));
        return info;
    }

    public async Task<List<Protocol.V1.WorldObject>> ListObjects() =>
        await Describe(await db.WorldObjects.Include(o => o.Owner).AsNoTracking().OrderBy(o => o.Id).ToListAsync());

    /// <summary>The objects as clients see them, with each owner's wealth tier, last day seen and colony age (one query for all owners).</summary>
    public async Task<List<Protocol.V1.WorldObject>> Describe(IReadOnlyList<Storage.Entities.WorldObject> objects)
    {
        var owners = objects.Select(o => o.OwnerPlayerId).Distinct().ToList();
        var info = await db.Players.AsNoTracking().Where(p => owners.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.WealthTotal,
                p.Account.LastSeenAt,
                p.Account.LastSessionEndAt,
                Tick = db.SaveVersions.Where(v => v.PlayerId == p.Id && v.IsCurrent).Select(v => (long?)v.GameTick).FirstOrDefault(),
            })
            .ToDictionaryAsync(x => x.Id);
        var tiers = hygiene.Value.Tiers;
        return objects.Select(o =>
        {
            var proto = ToProto(o);
            if (info.TryGetValue(o.OwnerPlayerId, out var i))
            {
                bool hasSave = i.Tick is not null;
                proto.WealthTier = WealthTiers.Of(i.WealthTotal, known: hasSave, tiers);
                if (Activity.Latest(i.LastSeenAt, i.LastSessionEndAt) is { } seen)
                    proto.OwnerLastSeen = Timestamp.FromDateTime(DateTime.SpecifyKind(seen.Date, DateTimeKind.Utc));
                if (hasSave) proto.ColonyGameDays = (int)(i.Tick!.Value / GameTime.TicksPerDay);
            }
            return proto;
        }).ToList();
    }

    /// <summary>The player's settlement as clients see it; null when they have none.</summary>
    public async Task<Protocol.V1.WorldObject?> DescribeSettlementOf(long playerId)
    {
        var settlement = await db.WorldObjects.Include(o => o.Owner).AsNoTracking()
            .FirstOrDefaultAsync(o => o.OwnerPlayerId == playerId && o.Type == WorldObjectTypes.Settlement);
        return settlement is null ? null : (await Describe([settlement]))[0];
    }

    public async Task<Protocol.V1.WorldObject> Settle(Session actor, int tile, string name)
    {
        var world = await db.World.AsNoTracking().FirstOrDefaultAsync() ?? throw RpcErrors.Failed(ErrorCode.WorldNotCreated, RefusalKeys.WorldNotCreated, "the world has not been created yet");
        if (!Names.IsValid(name)) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.NameInvalid, RefusalKeys.WorldSettlementNameInvalid, "settlement name is not a valid name");
        if (ParseTiles(world.NpcTiles).Contains(tile)) throw RpcErrors.Failed(ErrorCode.TileTaken, RefusalKeys.WorldNpcTile, "an NPC settlement occupies this tile");

        int owned = await db.WorldObjects.CountAsync(o => o.OwnerPlayerId == actor.PlayerId && o.Type == WorldObjectTypes.Settlement);
        if (owned >= server.Value.MaxSettlementsPerPlayer)
            throw RpcErrors.Failed(ErrorCode.SettlementLimit, RefusalKeys.WorldSettlementLimit, $"you already have {owned} settlement(s); the limit is {server.Value.MaxSettlementsPerPlayer}", owned, server.Value.MaxSettlementsPerPlayer);

        var entity = new Storage.Entities.WorldObject
        {
            Type = WorldObjectTypes.Settlement,
            OwnerPlayerId = actor.PlayerId,
            Tile = tile,
            Name = name,
            CreatedAt = DateTime.UtcNow,
        };
        db.WorldObjects.Add(entity);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            db.Entry(entity).State = EntityState.Detached;
            throw RpcErrors.Failed(ErrorCode.TileTaken, RefusalKeys.WorldTileTaken, "another player settled this tile first");
        }

        await db.Entry(entity).Reference(o => o.Owner).LoadAsync();
        var proto = (await Describe([entity]))[0];
        events.BroadcastExcept(actor.PlayerId, new ServerEvent { WorldObjectAdded = new WorldObjectAdded { Object = proto } });
        feed.Publish(FeedTypes.ColonyFounded, new ColonyData(new FeedPlayer(entity.OwnerPlayerId, entity.Owner.Name), name, tile));
        log.LogInformation("{Player} settled tile {Tile} as '{Name}'", actor.PlayerName, tile, name);
        return proto;
    }

    public async Task Abandon(Session actor, long objectId)
    {
        var entity = await db.WorldObjects.FindAsync(objectId) ?? throw RpcErrors.With(StatusCode.NotFound, ErrorCode.Unspecified, RefusalKeys.WorldObjectUnknown, "no such world object");
        if (entity.OwnerPlayerId != actor.PlayerId && actor.Role != Storage.Entities.Role.Admin) throw RpcErrors.With(StatusCode.PermissionDenied, ErrorCode.Unspecified, RefusalKeys.WorldNotOwned, "not your world object");

        db.WorldObjects.Remove(entity);
        await db.SaveChangesAsync();
        events.Broadcast(new ServerEvent { WorldObjectRemoved = new WorldObjectRemoved { ObjectId = objectId } });
        if (entity.Type == WorldObjectTypes.Settlement)
        {
            string owner = await db.Players.AsNoTracking().Where(pl => pl.Id == entity.OwnerPlayerId).Select(pl => pl.Name).FirstOrDefaultAsync() ?? "";
            feed.Publish(FeedTypes.ColonyRemoved, new ColonyData(new FeedPlayer(entity.OwnerPlayerId, owner), entity.Name, null));
        }
        log.LogInformation("{Player} abandoned world object {Id} on tile {Tile}", actor.PlayerName, objectId, entity.Tile);
    }

    public Task<bool> HasSettlement(long playerId) =>
        db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == playerId && o.Type == WorldObjectTypes.Settlement);

    public static Protocol.V1.WorldObject ToProto(Storage.Entities.WorldObject o) => new()
    {
        Id = o.Id,
        Type = o.Type,
        Owner = o.Owner.ToRef(),
        Tile = o.Tile,
        Name = o.Name,
        Payload = ByteString.CopyFrom(o.Payload),
    };

    public static IEnumerable<int> NpcTilesOf(WorldEntity world) => ParseTiles(world.NpcTiles);

    private static IEnumerable<int> ParseTiles(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse);
}
