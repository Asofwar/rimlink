using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;

namespace Rimlink.Server.Application.Economy;

/// <summary>Direct player-to-player transfers: items and pawns leave the sender's game, silver leaves their wallet, all into a parcel.</summary>
public sealed class TransferService(RimlinkDbContext db, WalletService wallet, MailboxService mailbox, ParcelValidator validator, EconomyGate gate, ILogger<TransferService> log)
{
    public async Task<Parcel> Send(long fromPlayerId, TransferRequest request)
    {
        gate.RequireOpen();
        if (request.ToPlayerId == fromPlayerId) throw RpcErrors.Failed(ErrorCode.SelfTrade, RefusalKeys.EconomySelfSend, "you cannot send to yourself");
        if (request.Silver < 0) throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.EconomyNegativeSilver, "silver must not be negative");
        if (validator.Validate(request.Items, request.Pawns) is { } reason) throw RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args);
        if (request.Items.Items.Count == 0 && request.Pawns.Pawns.Count == 0 && request.Silver == 0)
            throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.EconomyNothingToSend, "nothing to send");

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if (!string.IsNullOrEmpty(request.ClientRequestId))
        {
            var existing = await db.Parcels.AsNoTracking().FirstOrDefaultAsync(p => p.ClientRequestId == request.ClientRequestId);
            if (existing is not null) return existing;
        }
        // After the repeat check: a send that went through before a block must not be refused on its replay (the client would put the things back).
        await Social.BlockRules.RequireNotBlocked(db, fromPlayerId, request.ToPlayerId);
        await gate.RequireNotFrozen(fromPlayerId);

        var parcel = await mailbox.Create(request.ToPlayerId, fromPlayerId, ParcelKindDb.Gift, request.Items, request.Pawns, request.Silver,
            string.IsNullOrEmpty(request.ClientRequestId) ? null : request.ClientRequestId);
        if (request.Silver > 0) await wallet.Apply(fromPlayerId, -request.Silver, "send", parcel.Id);
        await StampOutgoing(fromPlayerId, request.GameTick);
        db.Transfers.Add(new Transfer
        {
            FromPlayerId = fromPlayerId,
            ToPlayerId = request.ToPlayerId,
            Kind = "send",
            GameTick = request.GameTick,
            Summary = Summary(request.Items, request.Pawns, request.Silver),
            At = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        wallet.FlushEvents();

        await mailbox.Announce(parcel);
        EconomyMetrics.Trade("send", request.Silver);
        log.LogInformation("Player {From} sent parcel {Parcel} to player {To}: {Summary}", fromPlayerId, parcel.Id, request.ToPlayerId, Summary(request.Items, request.Pawns, request.Silver));
        return parcel;
    }

    /// <summary>Records the tick of the save that accompanied an outgoing transfer; older saves are refused from now on.</summary>
    public async Task StampOutgoing(long playerId, long gameTick)
    {
        var player = await db.Players.FirstAsync(p => p.Id == playerId);
        if (gameTick > player.LastOutgoingTick) player.LastOutgoingTick = gameTick;
    }

    public static string Summary(ItemList? items, PawnList? pawns, long silver)
    {
        var parts = new List<string>();
        foreach (var i in items?.Items ?? []) parts.Add($"{i.Count}x {i.DefName}");
        foreach (var p in pawns?.Pawns ?? []) parts.Add($"pawn {p.Name} ({p.KindDef})");
        if (silver > 0) parts.Add($"{silver} silver");
        return string.Join(", ", parts);
    }
}
