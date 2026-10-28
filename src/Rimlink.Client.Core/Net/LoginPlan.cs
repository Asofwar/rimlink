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
    public static LoginAction Decide(bool pendingFlag, bool localSaveExists, bool serverHasSave, bool serverHasSettlement)
    {
        // A flagged local file is newer than anything on the server (its upload never completed);
        // an unflagged one with nothing on the server is the same case minus the flag (crash before it was written).
        if (localSaveExists && (pendingFlag || !serverHasSave)) return LoginAction.UploadLocalThenLoad;
        if (serverHasSave) return LoginAction.DownloadThenLoad;
        return serverHasSettlement ? LoginAction.AbandonThenNewColony : LoginAction.NewColony;
    }
}
