using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Social;

/// <summary>Typed trade calls over a connection; every call has a 30 s deadline. Open and SetSide go through the outbox.</summary>
public sealed class TradesClient(RimlinkConnection connection)
{
    private static DateTime Until => DateTime.UtcNow + Economy.EconomyClient.Deadline;

    public Task<Trade> Open(TradeOpenRequest request, CancellationToken ct) => connection.TradesApi.OpenAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Trade> SetSide(TradeSideRequest request, CancellationToken ct) => connection.TradesApi.SetSideAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Trade> Confirm(long tradeId, int revision, CancellationToken ct) =>
        connection.TradesApi.ConfirmAsync(new TradeConfirmRequest { TradeId = tradeId, Revision = revision }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Trade> Cancel(long tradeId, CancellationToken ct) => connection.TradesApi.CancelAsync(new TradeRef { TradeId = tradeId }, deadline: Until, cancellationToken: ct).ResponseAsync;
    /// <summary>Open trades plus those closed within the last 7 days, newest first; descriptors only.</summary>
    public Task<TradeList> List(CancellationToken ct) => connection.TradesApi.ListAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
}
