using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Grpc;

public static class RpcErrors
{
    public const string DetailTrailer = "rimlink-error-bin";

    public static RpcException Failed(ErrorCode code, string message) => With(StatusCode.FailedPrecondition, code, message);

    public static RpcException With(StatusCode status, ErrorCode code, string message)
    {
        var trailers = new Metadata { { DetailTrailer, new ErrorDetail { Code = code, Message = message }.ToByteArray() } };
        return new RpcException(new Status(status, message), trailers);
    }

    public static RpcException Unauthenticated(string message = "session required") =>
        new(new Status(StatusCode.Unauthenticated, message));

    public static RpcException PermissionDenied(string message) =>
        new(new Status(StatusCode.PermissionDenied, message));
}
