using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Economy;

/// <summary>Typed economy calls over a connection; every call carries the session token through the channel credentials and a 30 s deadline.</summary>
public sealed class EconomyClient(RimlinkConnection connection)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static DateTime Until => DateTime.UtcNow + Deadline;

    public Task<WalletState> Wallet(CancellationToken ct) => connection.WalletApi.GetAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<EconomyRules> Rules(CancellationToken ct) => connection.WalletApi.RulesAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<WalletHistory> History(CancellationToken ct) => connection.WalletApi.HistoryAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<WalletState> Deposit(DepositRequest request, CancellationToken ct) => connection.WalletApi.DepositAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Parcel> WithdrawSilver(WithdrawRequest request, CancellationToken ct) => connection.WalletApi.WithdrawAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;

    /// <summary>Descriptors only; <see cref="Get"/> fetches the blobs of one parcel.</summary>
    public Task<ParcelList> Pending(CancellationToken ct) => connection.MailboxApi.ListAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Parcel> Get(long parcelId, CancellationToken ct) => connection.MailboxApi.GetAsync(new ParcelRef { ParcelId = parcelId }, deadline: Until, cancellationToken: ct).ResponseAsync;
    /// <summary>Accept or refuse a gift awaiting a decision; a refused one goes back to its sender.</summary>
    public Task Answer(long parcelId, bool accept, CancellationToken ct) =>
        connection.MailboxApi.AnswerAsync(new ParcelAnswer { ParcelId = parcelId, Accept = accept }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task Deliver(long parcelId, long gameTick, CancellationToken ct) =>
        connection.MailboxApi.DeliverAsync(new DeliverRequest { ParcelId = parcelId, GameTick = gameTick }, deadline: Until, cancellationToken: ct).ResponseAsync;

    public Task<LotList> Lots(MarketQuery query, CancellationToken ct) => connection.MarketApi.ListAsync(query, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Lot> Post(PostRequest request, CancellationToken ct) => connection.MarketApi.PostAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Parcel> WithdrawLot(long lotId, CancellationToken ct) => connection.MarketApi.WithdrawAsync(new LotRef { LotId = lotId }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Parcel> Buy(long lotId, CancellationToken ct) => connection.MarketApi.BuyAsync(new LotRef { LotId = lotId }, deadline: Until, cancellationToken: ct).ResponseAsync;

    public Task<Parcel> Send(TransferRequest request, CancellationToken ct) => connection.TransferApi.SendAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;

    public static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>The server's error code from the "rimlink-error-bin" trailer, or null for a plain gRPC failure.</summary>
    public static ErrorCode? CodeOf(RpcException ex) => RpcErrorDetail.Read(ex).Code;

    /// <summary>The server's message when it sent one, otherwise the gRPC status.</summary>
    public static string MessageOf(RpcException ex) => RpcErrorDetail.Read(ex).Message;
}
