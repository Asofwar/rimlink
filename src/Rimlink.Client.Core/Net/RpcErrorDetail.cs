using Grpc.Core;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Net;

/// <summary>A server refusal as the client shows it: the code, the English message, and the key and arguments for the player's language.</summary>
public sealed record Refusal(ErrorCode? Code, string Message, string? Key, IReadOnlyList<string> Args);

public static class RpcErrorDetail
{
    public const string Trailer = "rimlink-error-bin";

    /// <summary>The refusal behind a failed call; without the trailer (an older server, a transport error) the status stands in for the message.</summary>
    public static Refusal Read(RpcException ex)
    {
        if (ex.Trailers.FirstOrDefault(t => t.Key == Trailer) is not { } trailer) return new Refusal(null, $"{ex.StatusCode}: {ex.Status.Detail}", null, []);
        var detail = ErrorDetail.Parser.ParseFrom(trailer.ValueBytes);
        return new Refusal(detail.Code, detail.Message, detail.Key.Length == 0 ? null : detail.Key, detail.Args.ToList());
    }

    /// <summary>Any failure as a refusal: a server refusal as above, anything else (a timeout, a transport error) by its message.</summary>
    public static Refusal Read(Exception ex) => ex is RpcException rpc ? Read(rpc) : new Refusal(null, ex.Message, null, []);
}
