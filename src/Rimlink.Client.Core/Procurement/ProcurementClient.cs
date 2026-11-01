using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
namespace Rimlink.Client.Procurement;

public sealed class ProcurementClient(RimlinkConnection connection)
{
    private static DateTime Until => DateTime.UtcNow + Economy.EconomyClient.Deadline;
    public Task<ProcurementList> List(ProcurementQuery request, CancellationToken ct) => connection.ProcurementApi.ListAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<ProcurementEntry> Create(ProcurementCreate request, CancellationToken ct) => connection.ProcurementApi.CreateAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<ProcurementEntry> Supply(ProcurementSupply request, CancellationToken ct) => connection.ProcurementApi.SupplyAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<ProcurementEntry> Cancel(ProcurementCancel request, CancellationToken ct) => connection.ProcurementApi.CancelAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
}
