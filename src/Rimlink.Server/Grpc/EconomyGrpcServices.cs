using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;

namespace Rimlink.Server.Grpc;

public sealed class WalletGrpcService(WalletService wallet, EconomyGate gate, TransferService transfers, MailboxService mailbox, ServerWealth wealth, EconomyGuard guard) : Protocol.V1.Wallet.WalletBase
{
    public override async Task<WalletState> Deposit(DepositRequest request, ServerCallContext context)
    {
        long playerId = context.GetSession().PlayerId;
        return new WalletState { Balance = await wallet.Deposit(playerId, request, transfers, gate, guard), ServerWealth = await wealth.Of(playerId) };
    }

    public override async Task<Parcel> Withdraw(WithdrawRequest request, ServerCallContext context) =>
        await mailbox.ToProto(await wallet.Withdraw(context.GetSession().PlayerId, request, mailbox, gate));

    public override async Task<WalletState> Get(Empty request, ServerCallContext context)
    {
        gate.RequireOpen();
        long playerId = context.GetSession().PlayerId;
        return new WalletState { Balance = await wallet.Balance(playerId), ServerWealth = await wealth.Of(playerId) };
    }

    public override async Task<WalletHistory> History(Empty request, ServerCallContext context)
    {
        gate.RequireOpen();
        var history = new WalletHistory();
        history.Entries.AddRange((await wallet.History(context.GetSession().PlayerId)).Select(e => new WalletEntryMsg
        {
            Id = e.Id,
            Delta = e.Delta,
            Kind = e.Kind,
            RefId = e.RefId,
            At = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.SpecifyKind(e.At, DateTimeKind.Utc)),
        }));
        return history;
    }
}

public sealed class MailboxGrpcService(MailboxService mailbox, GiftService gifts) : Protocol.V1.Mailbox.MailboxBase
{
    public override async Task<ParcelList> List(Empty request, ServerCallContext context)
    {
        var list = new ParcelList();
        foreach (var (parcel, redeliver) in await mailbox.Pending(context.GetSession().PlayerId))
            list.Parcels.Add(await mailbox.ToProto(parcel, redeliver, includeBlobs: false));
        return list;
    }

    public override async Task<Parcel> Get(ParcelRef request, ServerCallContext context) =>
        await mailbox.ToProto(await mailbox.Get(context.GetSession().PlayerId, request.ParcelId));

    public override async Task<Empty> Deliver(DeliverRequest request, ServerCallContext context)
    {
        await mailbox.Deliver(context.GetSession().PlayerId, request.ParcelId, request.GameTick);
        return new Empty();
    }

    public override async Task<Empty> Answer(ParcelAnswer request, ServerCallContext context)
    {
        await gifts.Answer(context.GetSession().PlayerId, request.ParcelId, request.Accept);
        return new Empty();
    }
}

public sealed class TransferGrpcService(TransferService transfers, MailboxService mailbox) : Protocol.V1.Transfer.TransferBase
{
    public override async Task<Parcel> Send(TransferRequest request, ServerCallContext context)
    {
        var parcel = await transfers.Send(context.GetSession().PlayerId, request);
        return await mailbox.ToProto(parcel);
    }
}
