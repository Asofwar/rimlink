using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Raids;

namespace Rimlink.Server.Grpc;

public sealed class RaidsGrpcService(RaidService raids, Application.Live.LiveSessionService live) : Protocol.V1.Raids.RaidsBase
{
    public override async Task<Raid> Launch(LaunchRequest request, ServerCallContext context) =>
        await raids.ToProto(await raids.Launch(context.GetSession().PlayerId, request), includeBlobs: false);

    public override async Task<RaidList> Pending(Empty request, ServerCallContext context) =>
        await raids.ToList(await raids.Pending(context.GetSession().PlayerId));

    public override async Task<RaidList> Mine(Empty request, ServerCallContext context) =>
        await raids.ToList(await raids.Mine(context.GetSession().PlayerId));

    public override async Task<Raid> Get(RaidRef request, ServerCallContext context) =>
        await raids.ToProto(await raids.Get(context.GetSession().PlayerId, request.RaidId), includeBlobs: true);

    public override async Task<Raid> Announce(RaidTick request, ServerCallContext context) =>
        await raids.ToProto(await raids.Announce(context.GetSession().PlayerId, request.RaidId, request.GameTick), includeBlobs: false);

    public override async Task<Empty> Land(RaidTick request, ServerCallContext context)
    {
        await raids.Land(context.GetSession().PlayerId, request.RaidId, request.GameTick);
        return new Empty();
    }

    public override async Task<Empty> Report(RaidReport request, ServerCallContext context)
    {
        long playerId = context.GetSession().PlayerId;
        if (request.RaidId == 0 && request.LiveSessionId != 0)
        {
            await live.ReportVisit(playerId, request); // a live visit has no raid row
            return new Empty();
        }
        var raid = await raids.Report(playerId, request);
        if (raid is not null && raid.Controller == (int)RaidController.ControllerLive) await live.Ended(raid.Id);
        return new Empty();
    }

    public override Task<PlayerFlags> Flags(Empty request, ServerCallContext context) => raids.Flags(context.GetSession().PlayerId);

    public override Task<PlayerFlags> SetPeaceful(PeacefulRequest request, ServerCallContext context) => raids.SetPeaceful(context.GetSession().PlayerId, request.Peaceful);

    public override Task<PlayerFlags> SetVisitsClosed(VisitsClosedRequest request, ServerCallContext context) => raids.SetVisitsClosed(context.GetSession().PlayerId, request.Closed);
}
