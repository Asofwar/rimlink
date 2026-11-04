using Microsoft.EntityFrameworkCore;
using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;

namespace Rimlink.Server.Application.Economy;

/// <summary>Direct player-to-player transfers: items and pawns leave the sender's game, silver leaves their wallet, all into a parcel.</summary>
public sealed class TransferService(RimlinkDbContext db, WalletService wallet, MailboxService mailbox, ParcelValidator validator, EconomyGate gate, EconomyGuard guard,
    ILogger<TransferService> log, IHttpContextAccessor http, Application.Outposts.PawnCustodyBridge custody)
{
    public async Task<Parcel> Send(long fromPlayerId, TransferRequest request)
    {
        await using var tx = await custody.BeginOutgoingAsync(fromPlayerId, request.ToPlayerId, request.Pawns,
            CustodyTransferKind.CustodyTransferSend, request.ClientRequestId, RequestIdentity.Hash(request), requestKind: OrdinaryRequestKind.TransferSend);
        if (await RequestIdentity.Parcel(db, fromPlayerId, "transfer.send", request.ClientRequestId, request) is { } existing) return existing;
        gate.RequireOpen();
        if (request.ToPlayerId == fromPlayerId) throw RpcErrors.Failed(ErrorCode.SelfTrade, RefusalKeys.EconomySelfSend, "you cannot send to yourself");
        if (request.Silver < 0) throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.EconomyNegativeSilver, "silver must not be negative");
        if (request.Items.Items.Count == 0 && request.Pawns.Pawns.Count == 0 && request.Silver == 0)
            throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.EconomyNothingToSend, "nothing to send");
        // After the repeat check: a send that went through before a block must not be refused on its replay (the client would put the things back).
        await Social.BlockRules.RequireNotBlocked(db, fromPlayerId, request.ToPlayerId);
        await gate.RequireNotFrozen(fromPlayerId);
        // After the replay check: a send the server already took is not refused because the modpack or the forbidden list
        // changed since (the client would return things that left).
        if (validator.Validate(request.Items, request.Pawns, checkForbidden: true) is { } reason) throw RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args);
        // A caravan hands over what already left the map; wallet silver and things from the map stay while under threat.
        bool fromMap = (request.Items.Items.Count > 0 || request.Pawns.Pawns.Count > 0) && !request.FromCaravan;
        if (fromMap || request.Silver > 0) await guard.RequireNoThreat(fromPlayerId);
        await guard.RequireNoDebt(fromPlayerId);

        await tx.ValidateAsync();
        var parcel = await mailbox.Create(request.ToPlayerId, fromPlayerId, ParcelKindDb.Gift, request.Items, request.Pawns, request.Silver,
            string.IsNullOrEmpty(request.ClientRequestId) ? null : request.ClientRequestId);
        parcel.RequestIntent = RequestIdentity.Hash(request);
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
        tx.Bind(parcel.Id, parcel.Id);
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
        await RequireCurrentSession(playerId);
        var player = await db.Players.FirstAsync(p => p.Id == playerId);
        await db.Entry(player).ReloadAsync();
        if (gameTick > player.LastOutgoingTick) player.LastOutgoingTick = gameTick;
    }

    /// <summary>Called inside the operation's database transaction, after the lookup of an already applied request.</summary>
    public async Task RequireCurrentSession(long playerId)
    {
        if (http.HttpContext?.Items[SessionInterceptor.ItemKey] is Application.Sessions.Session session && session.PlayerId == playerId)
        {
            long currentEpoch = await db.Players.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.SaveWriteEpoch).SingleAsync();
            if (currentEpoch != session.SaveWriteEpoch)
                throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.SaveWriteEpochMismatch, RefusalKeys.SavesWriteEpochMismatch,
                    "the outgoing operation belongs to the session before rollback");
            if (session.Cts.IsCancellationRequested) throw RpcErrors.Unauthenticated("the outgoing session ended");
        }
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
