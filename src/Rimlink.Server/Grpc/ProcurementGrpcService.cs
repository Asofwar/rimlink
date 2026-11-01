using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Procurement;
namespace Rimlink.Server.Grpc;

public sealed class ProcurementGrpcService(ProcurementService service) : Protocol.V1.Procurement.ProcurementBase
{
    public override Task<ProcurementList> List(ProcurementQuery r, ServerCallContext c) => service.List(c.GetSession().PlayerId, r);
    public override Task<ProcurementEntry> Create(ProcurementCreate r, ServerCallContext c) => service.Create(c.GetSession().PlayerId, r);
    public override Task<ProcurementEntry> Supply(ProcurementSupply r, ServerCallContext c) => service.Supply(c.GetSession().PlayerId, r);
    public override Task<ProcurementEntry> Cancel(ProcurementCancel r, ServerCallContext c) => service.Cancel(c.GetSession().PlayerId, r);
}
