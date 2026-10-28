using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Visits;

namespace Rimlink.Server.Grpc;

public sealed class VisitsGrpcService(VisitService visits) : Protocol.V1.Visits.VisitsBase
{
    public override async Task<VisitTicket> Begin(VisitRequest request, ServerCallContext context)
    {
        var (visit, host) = await visits.Begin(context.GetSession().PlayerId, request.TargetPlayerId);
        return new VisitTicket { VisitId = visit.Id, Host = host.ToRef() };
    }

    public override async Task<Empty> End(VisitRef request, ServerCallContext context)
    {
        await visits.End(context.GetSession().PlayerId, request.VisitId);
        return new Empty();
    }
}

public sealed class ShopGrpcService(ShopService shop, MailboxService mailbox) : Protocol.V1.Shop.ShopBase
{
    public override async Task<Empty> Publish(ShopList request, ServerCallContext context)
    {
        await shop.Publish(context.GetSession().PlayerId, request);
        return new Empty();
    }

    public override Task<ShopList> Of(PlayerRef request, ServerCallContext context) => shop.Of(request.PlayerId);

    public override async Task<Parcel> Buy(BuyRequest request, ServerCallContext context) =>
        await mailbox.ToProto(await shop.Buy(context.GetSession().PlayerId, request));

    public override async Task<RemovalList> PendingRemovals(Empty request, ServerCallContext context)
    {
        var list = new RemovalList();
        foreach (var r in await shop.Pending(context.GetSession().PlayerId)) list.Removals.Add(new Removal { Id = r.Id, ThingId = r.ThingId, Count = r.Count });
        return list;
    }

    public override async Task<Empty> AckRemoval(RemovalAck request, ServerCallContext context)
    {
        await shop.Ack(context.GetSession().PlayerId, request.Ids, request.GameTick);
        return new Empty();
    }
}
