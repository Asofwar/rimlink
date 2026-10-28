using RimWorld;
using Rimlink.Client.Net;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>The multiplayer save of this server: one file, uploaded after every write, re-tried on the next login if that failed.</summary>
public static class SaveSync
{
    public const string SaveNamePrefix = "Rimlink_";

    /// <summary>One file per server and account, so two accounts used from one PC never overwrite each other.</summary>
    public static string SaveFileName => $"{SaveNamePrefix}{RimlinkSession.Endpoint!.Id}_{RimlinkSession.MyPlayerId}";
    public static string SaveFilePath => GenFilePaths.FilePathForSavedGame(SaveFileName);
    public static string SaveDir => RimlinkSession.SaveDir(RimlinkSession.Endpoint!, RimlinkSession.MyPlayerId);
    public static PendingUpload Pending => new(SaveDir);
    public static bool IsSaving { get; private set; }
    public static DateTime? LastUploadUtc { get; private set; }
    public static string? LastError { get; private set; }
    /// <summary>Set when the server refused the last upload as older than a transfer; the game component shows the choice once.</summary>
    public static bool StaleSaveDetected { get; set; }

    private static Task? upload;

    public static bool IsOurs(string fileName) => RimlinkSession.Active && fileName == SaveFileName;

    /// <summary>Blocks until the upload started by the last save finishes (or the timeout passes); failures are already logged.</summary>
    public static void WaitForUpload(TimeSpan timeout)
    {
        try { upload?.Wait(timeout); }
        catch (AggregateException) { }
    }

    /// <summary>
    /// Writes the multiplayer file now (main thread) and marks it as not yet on the server. Used when the session is
    /// about to end without an upload (connection lost, replaced, player loads another save).
    /// </summary>
    public static void SaveLocallyForLater(string reason)
    {
        if (!RimlinkSession.Active || Current.Game == null || Current.ProgramState != ProgramState.Playing) return;
        var pending = Pending;
        try
        {
            GameDataSaveLoader.SaveGame(SaveFileName); // the postfix starts an upload that fails or is cancelled; the flag below covers both
            pending.Set();
            RimlinkLog.Instance.Info($"saved locally for a later upload ({reason})");
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] local save failed: " + ex);
        }
    }

    /// <summary>Called from the SaveGame postfix on the main thread; the upload runs in the background.</summary>
    public static void OnSaved(string fileName)
    {
        if (!IsOurs(fileName) || Find.World == null) return;
        var meta = new SaveMeta(Find.World.info.seedString, Faction.OfPlayer.Name, Find.TickManager.TicksGame);
        var connection = RimlinkSession.Connection!;
        // Captured now: the session (and with it Endpoint/MyPlayerId) may be over by the time the upload finishes.
        string path = SaveFilePath;
        var pending = Pending;
        upload = Task.Run(async () =>
        {
            try
            {
                var info = await new SaveTransfer(connection).UploadAsync(path, meta, CancellationToken.None);
                pending.Clear();
                LastUploadUtc = DateTime.UtcNow;
                LastError = null;
                RimlinkLog.Instance.Info($"save uploaded as v{info.Version}");
            }
            catch (Grpc.Core.RpcException ex) when (Rimlink.Client.Economy.EconomyClient.CodeOf(ex) == Rimlink.Protocol.V1.ErrorCode.SaveStale)
            {
                // Not a retry case: this file would bring back what was given away. The player decides in game.
                LastError = Rimlink.Client.Economy.EconomyClient.MessageOf(ex);
                StaleSaveDetected = true;
                RimlinkLog.Instance.Warn($"save refused as stale: {LastError}");
            }
            catch (Exception ex)
            {
                pending.Set();
                LastError = ex.Message;
                RimlinkLog.Instance.Warn($"save upload failed, will retry at next login: {ex.Message}");
            }
        });
    }

    /// <summary>Saves the multiplayer file (triggers the upload via the patch).</summary>
    public static void TriggerSave(string reason)
    {
        if (!RimlinkSession.Active || Current.Game == null || IsSaving) return;
        IsSaving = true;
        LongEventHandler.QueueLongEvent(() =>
        {
            try { GameDataSaveLoader.SaveGame(SaveFileName); }
            finally { IsSaving = false; }
        }, "Rimlink.Saving", doAsynchronously: false, ex => { IsSaving = false; Log.Error("[Rimlink] save failed: " + ex); });
        RimlinkLog.Instance.Info("saving (" + reason + ")");
    }
}
