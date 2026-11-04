using Grpc.Core;
using Rimlink.Protocol.V1;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.World;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Grpc;

public sealed class WorldGrpcService(WorldService worlds, IOptions<IntegrityOptions> integrity, IOptions<Hosting.HygieneOptions> hygiene, Application.Influence.InfluenceProcessEpoch process, OutpostManifestService manifests, OutpostManifestUploads uploads) : Protocol.V1.World.WorldBase
{
    public override async Task<WorldInfo> Create(CreateWorldRequest request, ServerCallContext context) =>
        WithPolicy(WorldService.ToInfo(await worlds.Create(request.Params ?? new WorldParams(), request.NpcTiles, context.GetSession(), context.CancellationToken)));

    public override async Task<WorldInfo> Get(Empty request, ServerCallContext context) =>
        WithPolicy(WorldService.ToInfo(await worlds.Get()));

    private WorldInfo WithPolicy(WorldInfo info)
    {
        info.ProcessInstanceId = process.Id;
        info.StorytellerLocked = info.Exists && integrity.Value.StorytellerPolicy == StorytellerPolicy.Locked;
        info.WealthTiers.AddRange(hygiene.Value.Tiers);
        return info;
    }

    public override async Task<WorldObjects> ListObjects(Empty request, ServerCallContext context)
    {
        var reply = new WorldObjects();
        reply.Objects.AddRange(await worlds.ListObjects());
        return reply;
    }

    public override Task<OutpostManifestBinding> GetOutpostManifestBinding(Empty request, ServerCallContext context) =>
        manifests.BindingAsync(Actor.From(context.GetSession()), context.CancellationToken);

    public override Task<ManifestPreview> StageOutpostManifest(OutpostManifestChunk request, ServerCallContext context) =>
        uploads.StageAsync(Actor.From(context.GetSession()), "grpc:account:" + context.GetSession().AccountId, request, manifests, context.CancellationToken);

    public override Task<OutpostManifestPage> ReadOutpostManifest(OutpostManifestPageRequest request, ServerCallContext context) =>
        manifests.ReadAsync(Actor.From(context.GetSession()), request, context.CancellationToken);

    public override Task<ManifestRevision> ConfirmOutpostManifest(OutpostManifestConfirmation request, ServerCallContext context) =>
        manifests.ConfirmAsync(Actor.From(context.GetSession()), request.ManifestId, request.ExpectedDigest, context.CancellationToken);

    public override Task<WorldObject> Settle(SettleRequest request, ServerCallContext context) =>
        worlds.Settle(context.GetSession(), request.Tile, request.Name);

    public override async Task<Empty> Abandon(AbandonRequest request, ServerCallContext context)
    {
        await worlds.Abandon(context.GetSession(), request.ObjectId);
        return new Empty();
    }
}
