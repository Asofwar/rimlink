using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.World;

namespace Rimlink.Server.Grpc;

public sealed class WorldGrpcService(WorldService worlds) : Protocol.V1.World.WorldBase
{
    public override async Task<WorldInfo> Create(CreateWorldRequest request, ServerCallContext context) =>
        WorldService.ToInfo(await worlds.Create(request.Params ?? new WorldParams(), request.NpcTiles, context.GetSession()));

    public override async Task<WorldInfo> Get(Empty request, ServerCallContext context) =>
        WorldService.ToInfo(await worlds.Get());

    public override async Task<WorldObjects> ListObjects(Empty request, ServerCallContext context)
    {
        var reply = new WorldObjects();
        reply.Objects.AddRange(await worlds.ListObjects());
        return reply;
    }

    public override Task<WorldObject> Settle(SettleRequest request, ServerCallContext context) =>
        worlds.Settle(context.GetSession(), request.Tile, request.Name);

    public override async Task<Empty> Abandon(AbandonRequest request, ServerCallContext context)
    {
        await worlds.Abandon(context.GetSession(), request.ObjectId);
        return new Empty();
    }
}
