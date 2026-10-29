using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class ReconnectPolicyTests
{
    [Fact]
    public void DelaysDoubleUpToSixtyAndThenRepeatWithoutEnd()
    {
        var delays = ReconnectPolicy.Default.Delays().Take(10).Select(d => (int)d.TotalSeconds).ToList();

        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60, 60], delays);
        Assert.Equal(TimeSpan.FromMinutes(2), ReconnectPolicy.Default.OfferExitAfter);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable, null, false)]
    [InlineData(StatusCode.Internal, null, false)]
    [InlineData(StatusCode.DeadlineExceeded, null, false)]
    [InlineData(StatusCode.ResourceExhausted, null, false)]
    [InlineData(StatusCode.Unknown, null, false)]
    [InlineData(StatusCode.FailedPrecondition, ErrorCode.ServerFull, false)]
    [InlineData(StatusCode.FailedPrecondition, ErrorCode.WorldNotCreated, false)]
    [InlineData(StatusCode.FailedPrecondition, ErrorCode.Banned, true)]
    [InlineData(StatusCode.FailedPrecondition, ErrorCode.WrongPassword, true)]
    [InlineData(StatusCode.FailedPrecondition, ErrorCode.ProtocolVersion, true)]
    [InlineData(StatusCode.Unauthenticated, null, true)]
    [InlineData(StatusCode.PermissionDenied, null, true)]
    [InlineData(StatusCode.InvalidArgument, ErrorCode.NameInvalid, true)]
    public void OnlyARefusalByTheServerEndsTheAttempts(StatusCode status, ErrorCode? code, bool refusal)
    {
        var trailers = new Metadata();
        if (code is { } c) trailers.Add("rimlink-error-bin", new ErrorDetail { Code = c, Message = "m" }.ToByteArray());

        Assert.Equal(refusal, ReconnectPolicy.IsRefusal(new RpcException(new Status(status, "x"), trailers)));
    }
}
