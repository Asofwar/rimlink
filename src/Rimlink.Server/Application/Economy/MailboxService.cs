using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;

namespace Rimlink.Server.Application.Economy;

/// <summary>Parcels waiting for a player. Delivery is confirmed by the recipient's game once the pod has landed.</summary>
public sealed class MailboxService(RimlinkDbContext db, EventBus events, WalletService wallet, IOptions<EconomyOptions> options)
{
    /// <summary>Creates a parcel inside the caller's transaction and announces it; the caller saves.</summary>
    public async Task<Parcel> Create(long toPlayerId, long? fromPlayerId, ParcelKindDb kind, ItemList? items, PawnList? pawns, long silver, string? clientRequestId, bool ignoreMailboxLimit = false)
    {
        if (!await db.Players.AnyAsync(p => p.Id == toPlayerId))
            throw RpcErrors.With(StatusCode.NotFound, ErrorCode.RecipientUnknown, $"no player with id {toPlayerId}");
        int pending = await db.Parcels.CountAsync(p => p.ToPlayerId == toPlayerId && p.DeliveredAt == null);
        // Returns of escrowed goods (lot withdrawn/expired) always land: refusing them would lose the seller's items.
        if (!ignoreMailboxLimit && pending >= options.Value.MaxPendingParcels)
            throw RpcErrors.Failed(ErrorCode.Unspecified, "recipient's mailbox is full");

        items ??= new ItemList();
        pawns ??= new PawnList();
        var parcel = new Parcel
        {
            ToPlayerId = toPlayerId,
            FromPlayerId = fromPlayerId,
            Kind = kind,
            Items = items.ToByteArray(),
            Pawns = pawns.ToByteArray(),
            Silver = silver,
            SizeBytes = ParcelValidator.SizeOf(items, pawns),
            CreatedAt = DateTime.UtcNow,
            ClientRequestId = clientRequestId,
        };
        db.Parcels.Add(parcel);
        await db.SaveChangesAsync();
        return parcel;
    }

    /// <summary>After the transaction committed: tell the recipient's live session, if any.</summary>
    public async Task Announce(Parcel parcel)
    {
        events.SendTo(parcel.ToPlayerId, new ServerEvent { ParcelArrived = new ParcelArrived { Parcel = await ToProto(parcel) } });
    }

    /// <summary>Undelivered parcels, plus delivered ones the player's current save predates (flagged for redelivery).</summary>
    public async Task<List<(Parcel Parcel, bool Redeliver)>> Pending(long playerId)
    {
        long saveTick = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == playerId && v.IsCurrent).Select(v => v.GameTick).FirstOrDefaultAsync();
        var parcels = await db.Parcels.AsNoTracking()
            .Where(p => p.ToPlayerId == playerId && (p.DeliveredAt == null || p.DeliveredTick > saveTick))
            .OrderBy(p => p.Id).ToListAsync();
        return parcels.Select(p => (p, p.DeliveredAt != null)).ToList();
    }

    /// <summary>Idempotent: the first call credits the parcel's silver (except withdrawals, which become physical silver in the game).</summary>
    public async Task Deliver(long playerId, long parcelId, long gameTick)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var parcel = await db.Parcels.FirstOrDefaultAsync(p => p.Id == parcelId && p.ToPlayerId == playerId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "no such parcel in your mailbox"));
        if (parcel.DeliveredAt is null)
        {
            parcel.DeliveredAt = DateTime.UtcNow;
            parcel.DeliveredTick = gameTick;
            if (parcel.Silver > 0 && parcel.Kind != ParcelKindDb.Withdraw)
                await wallet.Apply(playerId, parcel.Silver, "receive", parcel.Id);
            EconomyMetrics.Trade("deliver");
            db.Transfers.Add(new Transfer { FromPlayerId = playerId, ToPlayerId = null, Kind = "deliver", GameTick = gameTick, Summary = $"parcel {parcel.Id}", At = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        else
        {
            // Redelivered (the client saved before the first delivery): remember the newer tick so List stops offering it.
            parcel.DeliveredTick = Math.Max(parcel.DeliveredTick ?? 0, gameTick);
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<Protocol.V1.Parcel> ToProto(Parcel p, bool redeliver = false)
    {
        var proto = new Protocol.V1.Parcel
        {
            Id = p.Id,
            Kind = (ParcelKind)p.Kind,
            Items = ItemList.Parser.ParseFrom(p.Items),
            Pawns = PawnList.Parser.ParseFrom(p.Pawns),
            Silver = p.Silver,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc)),
            Redeliver = redeliver,
            DeliveredTick = p.DeliveredTick ?? 0,
            SizeBytes = p.SizeBytes,
        };
        if (p.FromPlayerId is { } fromId)
        {
            var from = await db.Players.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fromId);
            if (from is not null) proto.From = from.ToRef();
        }
        return proto;
    }
}
