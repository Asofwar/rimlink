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

    private static Task? upload;

    public static bool IsOurs(string fileName) => RimlinkSession.Active && fileName == SaveFileName;

    /// <summary>Blocks until the upload started by the last save finishes (or the timeout passes); failures are already logged.</summary>
    public static void WaitForUpload(TimeSpan timeout)
    {
        try { upload?.Wait(timeout); }
        catch (AggregateException) { }
    }

    /// <summary>Called from the SaveGame postfix on the main thread; the upload runs in the background.</summary>
    public static void OnSaved(string fileName)
    {
        if (!IsOurs(fileName) || Find.World == null) return;
        var meta = new SaveMeta(Find.World.info.seedString, Faction.OfPlayer.Name, Find.TickManager.TicksGame);
        var connection = RimlinkSession.Connection!;
        string path = SaveFilePath;
        upload = Task.Run(async () =>
        {
            try
            {
                var info = await new SaveTransfer(connection).UploadAsync(path, meta, CancellationToken.None);
                Pending.Clear();
                LastUploadUtc = DateTime.UtcNow;
                LastError = null;
                RimlinkLog.Instance.Info($"save uploaded as v{info.Version}");
            }
            catch (Exception ex)
            {
                Pending.Set();
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
