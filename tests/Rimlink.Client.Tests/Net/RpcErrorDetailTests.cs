using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Net;

/// <summary>Live UX spec §5: the client reads a refusal's key and arguments, and an older server's refusal still reads.</summary>
public class RpcErrorDetailTests
{
    private static RpcException Refused(ErrorDetail detail) =>
        new(new Status(StatusCode.FailedPrecondition, detail.Message), new Metadata { { "rimlink-error-bin", detail.ToByteArray() } });

    [Fact]
    public void ReadGivesKeyAndArgs()
    {
        var detail = new ErrorDetail { Code = ErrorCode.LiveActive, Message = "Hosta is in a live session already", Key = "Live.HostBusy" };
        detail.Args.Add("Hosta");

        var refusal = RpcErrorDetail.Read(Refused(detail));

        Assert.Equal((ErrorCode.LiveActive, "Hosta is in a live session already", "Live.HostBusy"), (refusal.Code, refusal.Message, refusal.Key));
        Assert.Equal(["Hosta"], refusal.Args);
    }

    [Fact]
    public void ReadWithoutKeyGivesTheMessage()
    {
        var refusal = RpcErrorDetail.Read(Refused(new ErrorDetail { Code = ErrorCode.Muted, Message = "you are muted" }));

        Assert.Equal((ErrorCode.Muted, "you are muted"), (refusal.Code, refusal.Message));
        Assert.Null(refusal.Key);
        Assert.Empty(refusal.Args);
    }

    [Fact]
    public void ReadWithoutTrailerGivesStatus()
    {
        var refusal = RpcErrorDetail.Read(new RpcException(new Status(StatusCode.FailedPrecondition, "x")));

        Assert.Equal("FailedPrecondition: x", refusal.Message);
        Assert.Null(refusal.Code);
        Assert.Null(refusal.Key);
    }
}
