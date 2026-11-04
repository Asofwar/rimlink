using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

/// <summary>Bind external parcel contents into the immutable typed payload before its own digest is computed.</summary>
public static class OutpostParcelContents
{
    public static void Bind(OutpostParcelPayload payload, ItemList? items, PawnList? pawns, long silver)
    {
        payload.PawnListSha256 = Hash(pawns ?? new PawnList());
        payload.ItemListSha256 = Hash(items ?? new ItemList());
        payload.BoundSilver = silver;
    }

    public static bool Match(OutpostParcelPayload payload, ItemList? items, PawnList? pawns, long silver)
        => payload.PawnListSha256.Length == 32 && payload.ItemListSha256.Length == 32
            && payload.BoundSilver == silver && silver >= 0
            && payload.PawnListSha256.Equals(Hash(pawns ?? new PawnList()))
            && payload.ItemListSha256.Equals(Hash(items ?? new ItemList()));

    private static ByteString Hash(IMessage message) => ByteString.CopyFrom(SHA256.HashData(message.ToByteArray()));
}
