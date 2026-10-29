using System.Globalization;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Grpc;

public static class RpcErrors
{
    public const string DetailTrailer = "rimlink-error-bin";

    public static RpcException Failed(ErrorCode code, string message) => With(StatusCode.FailedPrecondition, code, message);

    public static RpcException With(StatusCode status, ErrorCode code, string message) => Build(status, new ErrorDetail { Code = code, Message = message });

    /// <summary>A refusal the client shows by <paramref name="key"/> (<see cref="RefusalKeys"/>); <paramref name="message"/> stays the English text.</summary>
    public static RpcException Failed(ErrorCode code, string key, string message, params object[] args) => With(StatusCode.FailedPrecondition, code, key, message, args);

    public static RpcException With(StatusCode status, ErrorCode code, string key, string message, params object[] args)
    {
        var detail = new ErrorDetail { Code = code, Message = message, Key = key };
        detail.Args.AddRange(args.Select(Arg));
        return Build(status, detail);
    }

    private static string Arg(object value) => value switch
    {
        DateTime at => at.ToString("u", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static RpcException Build(StatusCode status, ErrorDetail detail)
    {
        if (detail.Code >= ErrorCode.WalletInsufficient) Application.Economy.EconomyMetrics.Error(detail.Code);
        return new RpcException(new Status(status, detail.Message), new Metadata { { DetailTrailer, detail.ToByteArray() } });
    }

    public static RpcException Unauthenticated(string message = "session required") =>
        new(new Status(StatusCode.Unauthenticated, message));

    public static RpcException PermissionDenied(string message) =>
        new(new Status(StatusCode.PermissionDenied, message));
}
