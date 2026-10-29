using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Incidents;

namespace Rimlink.Server.Grpc;

public sealed class IncidentsGrpcService(IncidentService incidents) : Protocol.V1.Incidents.IncidentsBase
{
    public override Task<IncidentCatalog> Catalog(Empty request, ServerCallContext context) => Task.FromResult(incidents.Catalog());

    public override Task<IncidentQuote> Quote(QuoteRequest request, ServerCallContext context) => incidents.Quote(context.GetSession().PlayerId, request);

    public override async Task<Incident> Order(OrderIncidentRequest request, ServerCallContext context) =>
        await incidents.ToProto(await incidents.Order(context.GetSession().PlayerId, request));

    public override async Task<IncidentList> Pending(Empty request, ServerCallContext context) =>
        await ListOf(await incidents.Pending(context.GetSession().PlayerId));

    public override async Task<IncidentList> Mine(Empty request, ServerCallContext context) =>
        await ListOf(await incidents.Mine(context.GetSession().PlayerId));

    private async Task<IncidentList> ListOf(IEnumerable<Storage.Entities.IncidentOrder> orders)
    {
        var list = new IncidentList();
        foreach (var o in orders) list.Incidents.Add(await incidents.ToProto(o));
        return list;
    }
}
