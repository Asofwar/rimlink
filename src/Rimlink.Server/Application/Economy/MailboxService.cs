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
        var recipient = await db.Players.AsNoTracking().Where(p => p.Id == toPlayerId).Select(p => new { p.AcceptGifts }).FirstOrDefaultAsync()
            ?? throw RpcErrors.With(StatusCode.NotFound, ErrorCode.RecipientUnknown, RefusalKeys.EconomyRecipientUnknown, $"no player with id {toPlayerId}", toPlayerId);
        // Gifts waiting for an answer count against further gifts only: they must not lock purchases, returns or withdrawals.
        bool gift = kind == ParcelKindDb.Gift;
        int pending = await db.Parcels.CountAsync(p => p.ToPlayerId == toPlayerId && p.DeliveredAt == null && (gift || p.Decision != ParcelDecisionDb.Awaiting));
        // Returns of escrowed goods (lot withdrawn/expired) always land: refusing them would lose the seller's items.
        if (!ignoreMailboxLimit && pending >= options.Value.MaxPendingParcels)
            throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.EconomyMailboxFull, "recipient's mailbox is full");

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
            // A player's gift waits for the recipient's decision unless they take gifts without asking.
            Decision = kind == ParcelKindDb.Gift && fromPlayerId is not null
                ? recipient.AcceptGifts ? ParcelDecisionDb.Accepted : ParcelDecisionDb.Awaiting
                : ParcelDecisionDb.None,
        };
        db.Parcels.Add(parcel);
        await db.SaveChangesAsync();
        return parcel;
    }

    /// <summary>After the transaction committed: tell the recipient's live session, if any (descriptor only; the blob comes with Get).</summary>
    public async Task Announce(Parcel parcel)
    {
        events.SendTo(parcel.ToPlayerId, new ServerEvent { ParcelArrived = new ParcelArrived { Parcel = await ToProto(parcel, includeBlobs: false) } });
    }

    /// <summary>One parcel of the player with its blobs; a gift waiting for a decision has none to give (a client that does not know decisions would drop it).</summary>
    public async Task<Parcel> Get(long playerId, long parcelId) =>
        await db.Parcels.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parcelId && p.ToPlayerId == playerId && p.Decision != ParcelDecisionDb.Awaiting)
        ?? throw RpcErrors.With(StatusCode.NotFound, ErrorCode.Unspecified, RefusalKeys.EconomyParcelUnknown, "no such parcel in your mailbox");

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
            ?? throw RpcErrors.With(StatusCode.NotFound, ErrorCode.Unspecified, RefusalKeys.EconomyParcelUnknown, "no such parcel in your mailbox");
        if (parcel.Decision == ParcelDecisionDb.Awaiting) throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.Unspecified, RefusalKeys.GiftsAwaiting, "the gift awaits a decision");
        if (parcel.DeliveredAt is null)
        {
            parcel.DeliveredAt = DateTime.UtcNow;
            parcel.DeliveredTick = gameTick;
            if (parcel.Silver > 0 && parcel.Kind != ParcelKindDb.Withdraw)
                await wallet.Apply(playerId, parcel.Silver, "receive", parcel.Id);
            EconomyMetrics.Trade("deliver");
            db.Transfers.Add(new Transfer { FromPlayerId = playerId, ToPlayerId = null, Kind = "deliver", GameTick = gameTick, Summary = $"parcel {parcel.Id}", At = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            wallet.FlushEvents();
            return;
        }
        else
        {
            // Redelivered (the client saved before the first delivery): remember the newer tick so List stops offering it.
            parcel.DeliveredTick = Math.Max(parcel.DeliveredTick ?? 0, gameTick);
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
    }

    /// <summary>Lists and events carry descriptors only: blobs of a full mailbox would exceed the gRPC message size.</summary>
    public static ItemList StripBlobs(ItemList items, bool includeBlobs)
    {
        if (includeBlobs) return items;
        foreach (var i in items.Items) i.ScribeXmlZstd = ByteString.Empty;
        return items;
    }

    public static PawnList StripBlobs(PawnList pawns, bool includeBlobs)
    {
        if (includeBlobs) return pawns;
        foreach (var p in pawns.Pawns) p.ScribeXmlZstd = ByteString.Empty;
        return pawns;
    }

    public static PawnBlob StripBlobs(PawnBlob pawn, bool includeBlobs)
    {
        if (!includeBlobs) pawn.ScribeXmlZstd = ByteString.Empty;
        return pawn;
    }

    public async Task<Protocol.V1.Parcel> ToProto(Parcel p, bool redeliver = false, bool includeBlobs = true)
    {
        var proto = new Protocol.V1.Parcel
        {
            Id = p.Id,
            Kind = (ParcelKind)p.Kind,
            Items = StripBlobs(ItemList.Parser.ParseFrom(p.Items), includeBlobs),
            Pawns = StripBlobs(PawnList.Parser.ParseFrom(p.Pawns), includeBlobs),
            Silver = p.Silver,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc)),
            Redeliver = redeliver,
            DeliveredTick = p.DeliveredTick ?? 0,
            SizeBytes = p.SizeBytes,
            AwaitingDecision = p.Decision == ParcelDecisionDb.Awaiting,
        };
        if (p.FromPlayerId is { } fromId)
        {
            var from = await db.Players.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fromId);
            if (from is not null) proto.From = from.ToRef();
        }
        return proto;
    }
}
