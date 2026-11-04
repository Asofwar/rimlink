using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Outposts;

public static class OutpostRequestIdentity
{
    public static ByteString Digest(OutpostPrepareRequest request)
    {
        var copy = request.Clone(); copy.IntentDigest = ByteString.Empty;
        return ByteString.CopyFrom(SHA256.HashData(copy.ToByteArray()));
    }
}
