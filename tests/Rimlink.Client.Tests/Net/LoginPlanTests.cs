using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class LoginPlanTests
{
    [Theory]
    // The last upload never reached the server: the local file is the newest copy, whatever the server holds.
    [InlineData(true, true, true, true, LoginAction.UploadLocalThenLoad)]
    [InlineData(true, true, false, false, LoginAction.UploadLocalThenLoad)]
    // No flag, but a local file and nothing on the server (crash between the save and the flag): still ours to send.
    [InlineData(false, true, false, true, LoginAction.UploadLocalThenLoad)]
    // Normal return: the server copy is loaded.
    [InlineData(false, true, true, true, LoginAction.DownloadThenLoad)]
    [InlineData(false, false, true, true, LoginAction.DownloadThenLoad)]
    // Settled but never saved anywhere: free the tile, start over.
    [InlineData(false, false, false, true, LoginAction.AbandonThenNewColony)]
    [InlineData(false, false, false, false, LoginAction.NewColony)]
    public void DecidesWhatToDoAtLogin(bool pendingFlag, bool localSaveExists, bool serverHasSave, bool serverHasSettlement, LoginAction expected)
    {
        Assert.Equal(expected, LoginPlan.Decide(pendingFlag, localSaveExists, serverHasSave, serverHasSettlement));
    }

    [Fact]
    public void FlagWithoutAFileIsNotAnUpload()
    {
        Assert.Equal(LoginAction.DownloadThenLoad, LoginPlan.Decide(pendingFlag: true, localSaveExists: false, serverHasSave: true, serverHasSettlement: true));
        Assert.Equal(LoginAction.NewColony, LoginPlan.Decide(pendingFlag: true, localSaveExists: false, serverHasSave: false, serverHasSettlement: false));
    }

    [Theory]
    [InlineData(false, false, false, LoginAction.NewColony)]
    [InlineData(true, false, false, LoginAction.NewColony)]
    [InlineData(true, false, true, LoginAction.AbandonThenNewColony)]
    [InlineData(true, true, true, LoginAction.DownloadThenLoad)]
    [InlineData(false, true, true, LoginAction.DownloadThenLoad)]
    public void AFileFromBeforeTheResetIsNeverUploaded(bool pending, bool serverSave, bool settlement, LoginAction expected)
    {
        Assert.Equal(expected, LoginPlan.Decide(pending, true, serverSave, settlement, localSaveGeneration: 0, serverSaveGeneration: 1));
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(0L, true)]
    [InlineData(7L, false)]
    [InlineData(7L, true)]
    public void RecoveryOfTheSameColonyWorksEvenBeforeThePendingFlag(long generation, bool pending)
    {
        Assert.Equal(LoginAction.UploadLocalThenLoad,
            LoginPlan.Decide(pending, true, false, false, localSaveGeneration: generation, serverSaveGeneration: generation));
    }

    [Theory]
    [InlineData(StatusCode.ResourceExhausted)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.Unknown)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public void TemporaryUploadRefusalsKeepTheRecoveryCopy(StatusCode status)
    {
        Assert.False(LoginPlan.IsPermanentUploadRefusal(new RpcException(new Status(status, "retry later"))));
    }

    [Theory]
    [InlineData(ErrorCode.SaveInvalid)]
    [InlineData(ErrorCode.SaveStale)]
    [InlineData(ErrorCode.SaveGenerationMismatch)]
    public void PermanentSaveRefusalsKeepTheFileSeparately(ErrorCode code)
    {
        var error = new ErrorDetail { Code = code, Message = "not this colony" };
        var refused = new RpcException(new Status(StatusCode.FailedPrecondition, error.Message),
            new Metadata { { RpcErrorDetail.Trailer, error.ToByteArray() } });

        Assert.True(LoginPlan.IsPermanentUploadRefusal(refused));
    }
}
