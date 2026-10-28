using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Visits;

/// <summary>Visit tickets and the host's save; 30 s deadlines on the unary calls.</summary>
public sealed class VisitClient(RimlinkConnection connection)
{
    private static DateTime Until => DateTime.UtcNow + Economy.EconomyClient.Deadline;

    public Task<VisitTicket> Begin(long targetPlayerId, long gameTick, CancellationToken ct) =>
        connection.VisitsApi.BeginAsync(new VisitRequest { TargetPlayerId = targetPlayerId, GameTick = gameTick }, deadline: Until, cancellationToken: ct).ResponseAsync;

    public Task End(long visitId, CancellationToken ct) =>
        connection.VisitsApi.EndAsync(new VisitRef { VisitId = visitId }, deadline: Until, cancellationToken: ct).ResponseAsync;

    /// <summary>The host's current save into <paramref name="targetPath"/> (temp file + rename); needs an open visit.</summary>
    public Task<string?> DownloadOf(long hostPlayerId, string targetPath, CancellationToken ct) =>
        new SaveTransfer(connection).DownloadOfAsync(hostPlayerId, targetPath, ct);
}

/// <summary>The shop: what a host sells, what a visitor buys, what the host still has to take off its map.</summary>
public sealed class ShopClient(RimlinkConnection connection)
{
    private static DateTime Until => DateTime.UtcNow + Economy.EconomyClient.Deadline;

    public Task Publish(ShopList list, CancellationToken ct) => connection.ShopApi.PublishAsync(list, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<ShopList> Of(long hostPlayerId, CancellationToken ct) =>
        connection.ShopApi.OfAsync(new PlayerRef { PlayerId = hostPlayerId }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Parcel> Buy(BuyRequest request, CancellationToken ct) => connection.ShopApi.BuyAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<RemovalList> PendingRemovals(CancellationToken ct) => connection.ShopApi.PendingRemovalsAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task AckRemoval(RemovalAck ack, CancellationToken ct) => connection.ShopApi.AckRemovalAsync(ack, deadline: Until, cancellationToken: ct).ResponseAsync;
}
