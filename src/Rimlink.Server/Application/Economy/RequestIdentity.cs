using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;

namespace Rimlink.Server.Application.Economy;

/// <summary>A request key belongs to its actor and operation; only its original intent may replay.</summary>
public static class RequestIdentity
{
    public static byte[] Hash(IMessage request) => SHA256.HashData(request.ToByteArray());
    public static void Verify(byte[]? original, IMessage request)
    {
        // An old row did not record the original intent: do not invent one from a replay or mutated escrow.
        if (original is not null && !original.AsSpan().SequenceEqual(Hash(request))) throw Mismatch();
    }
    public static RpcException Mismatch() => RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.RequestIntentMismatch,
        RefusalKeys.RequestIntentMismatch, "the request key belongs to a different intent");
    public static async Task<Parcel?> Parcel(RimlinkDbContext db, long actor, string kind, string id, IMessage request)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var found = await db.Parcels.AsNoTracking().FirstOrDefaultAsync(p => p.RequestActorId == actor && p.RequestKind == kind && p.ClientRequestId == id);
        if (found is not null) { Verify(found.RequestIntent, request); return found; }
        if (await db.Parcels.AsNoTracking().AnyAsync(p => p.ClientRequestId == id && (p.RequestActorId == null || p.RequestKind == null)))
            throw new RpcException(new Status(StatusCode.Unavailable, "a legacy request with unresolved ownership requires recovery"));
        return null;
    }
    public static long TradeTarget(Transfer receipt)
    {
        if (receipt.RequestTargetId is > 0) return receipt.RequestTargetId.Value;
        string[] parts = receipt.Summary.Split(' ', 4);
        if (parts.Length == 4 && parts[0] == "trade" && parts[2].StartsWith('r') && parts[2].EndsWith(':')
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0
            && id.ToString(CultureInfo.InvariantCulture) == parts[1]
            && int.TryParse(parts[2].AsSpan(1, parts[2].Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int revision) && revision > 0)
            return id;
        throw new RpcException(new Status(StatusCode.Unavailable, "the legacy trade request requires manual recovery"));
    }
}
