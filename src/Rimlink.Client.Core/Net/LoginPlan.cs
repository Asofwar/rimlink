using Grpc.Core;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Net;

public enum LoginAction
{
    UploadLocalThenLoad,
    DownloadThenLoad,
    AbandonThenNewColony,
    NewColony,
}

/// <summary>What to do with saves right after a login, from the local marker and the server's reply.</summary>
public static class LoginPlan
{
    public static LoginAction Decide(bool pendingFlag, bool localSaveExists, bool serverHasSave, bool serverHasSettlement,
        long localSaveGeneration = 0, long serverSaveGeneration = 0)
    {
        // A flagged local file is newer than anything on the server (its upload never completed);
        // an unflagged one with nothing on the server is the same case minus the flag (crash before it was written).
        if (localSaveExists && localSaveGeneration == serverSaveGeneration && (pendingFlag || !serverHasSave)) return LoginAction.UploadLocalThenLoad;
        if (serverHasSave) return LoginAction.DownloadThenLoad;
        return serverHasSettlement ? LoginAction.AbandonThenNewColony : LoginAction.NewColony;
    }

    /// <summary>A rate limit, lost session or transport failure must leave the local file available for another attempt.</summary>
    public static bool IsPermanentUploadRefusal(RpcException ex) =>
        ex.StatusCode == StatusCode.InvalidArgument ||
        RpcErrorDetail.Read(ex).Code is ErrorCode.SaveInvalid or ErrorCode.SaveStale or ErrorCode.SaveGenerationMismatch or ErrorCode.SaveWriteEpochMismatch;
}
