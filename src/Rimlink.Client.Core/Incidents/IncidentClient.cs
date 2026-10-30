using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Incidents;

/// <summary>Typed paid incident calls over a connection; the session token travels in the channel credentials, every call has a 30 s deadline.</summary>
public sealed class IncidentClient(RimlinkConnection connection)
{
    private static DateTime Until => DateTime.UtcNow + Economy.EconomyClient.Deadline;

    public Task<IncidentCatalog> Catalog(CancellationToken ct) => connection.IncidentsApi.CatalogAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<IncidentQuote> Quote(QuoteRequest request, CancellationToken ct) => connection.IncidentsApi.QuoteAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Incident> Order(OrderIncidentRequest request, CancellationToken ct) => connection.IncidentsApi.OrderAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    /// <summary>Incidents waiting for my colony.</summary>
    public Task<IncidentList> Pending(CancellationToken ct) => connection.IncidentsApi.PendingAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    /// <summary>Incidents I ordered, newest first.</summary>
    public Task<IncidentList> Mine(CancellationToken ct) => connection.IncidentsApi.MineAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Incident> Announce(long incidentId, long gameTick, CancellationToken ct) =>
        connection.IncidentsApi.AnnounceAsync(new AnnounceIncident { IncidentId = incidentId, GameTick = gameTick }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task Report(long incidentId, bool fired, string reason, CancellationToken ct) =>
        connection.IncidentsApi.ReportAsync(new IncidentReport { IncidentId = incidentId, Fired = fired, Reason = reason }, deadline: Until, cancellationToken: ct).ResponseAsync;
}
