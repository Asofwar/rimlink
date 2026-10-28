using Rimlink.Client.Net;
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
}
