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
    /// <summary>Server version of the last successful upload (0 before the first).</summary>
    public static int LastVersion { get; private set; }
    /// <summary>Set when the server refused the last upload as older than a transfer; the game component shows the choice once.</summary>
    public static bool StaleSaveDetected { get; set; }

    private static Task? upload;

    public static bool IsOurs(string fileName) => RimlinkSession.Active && fileName == SaveFileName;

    /// <summary>
    /// Blocks until the upload started by the last save finishes. False when no upload started (the save failed) or the
    /// timeout passed; a finished upload that failed is reported through <see cref="LastError"/>.
    /// </summary>
    public static bool WaitForUpload(TimeSpan timeout)
    {
        if (upload is not { } task) return false;
        try { return task.Wait(timeout); }
        catch (AggregateException) { return true; }
    }

    /// <summary>
    /// Writes the multiplayer file now (main thread) and marks it as not yet on the server. Used when the session is
    /// about to end without an upload (connection lost, replaced, player loads another save). False when nothing was saved.
    /// </summary>
    public static bool SaveLocallyForLater(string reason)
    {
        if (!RimlinkSession.Active || Current.Game == null || Current.ProgramState != ProgramState.Playing || Visits.VisitSession.InVisit) return false; // a visit's game is the host's copy; the own colony is already uploaded
        var pending = Pending;
        try
        {
            GameDataSaveLoader.SaveGame(SaveFileName); // the postfix starts an upload that fails or is cancelled; the flag below covers both
            pending.Set();
            RimlinkLog.Instance.Info($"saved locally for a later upload ({reason})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] local save failed: " + ex);
            return false;
        }
    }

    /// <summary>Called from the SaveGame postfix on the main thread; the upload runs in the background.</summary>
    public static void OnSaved(string fileName)
    {
        if (!IsOurs(fileName) || Find.World == null || Visits.VisitSession.Active) return;
        var home = Find.AnyPlayerHomeMap;
        var meta = new SaveMeta(Find.World.info.seedString, Faction.OfPlayer.Name, Find.TickManager.TicksGame,
            (long)(home?.wealthWatcher.WealthTotal ?? 0f), (long)(home?.wealthWatcher.WealthItems ?? 0f));
        var shopList = Shop.ShopSync.Build(meta.GameTick);
        var connection = RimlinkSession.Connection!;
        // Captured now: the session (and with it Endpoint/MyPlayerId) may be over by the time the upload finishes.
        string path = SaveFilePath;
        var pending = Pending;
        upload = Task.Run(async () =>
        {
            // Bounded: a stream the server already refused can leave a write hanging, and callers wait on this task.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                var info = await new SaveTransfer(connection).UploadAsync(path, meta, timeout.Token);
                pending.Clear();
                LastUploadUtc = DateTime.UtcNow;
                LastError = null;
                LastVersion = info.Version;
                RimlinkLog.Instance.Info($"save uploaded as v{info.Version}");
                if (shopList is not null) await Shop.ShopSync.Publish(shopList, connection);
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

    /// <summary>Saves the multiplayer file (triggers the upload via the patch). False when nothing was queued.</summary>
    public static bool TriggerSave(string reason)
    {
        if (!RimlinkSession.Active || Current.Game == null || IsSaving || Visits.VisitSession.InVisit) return false;
        IsSaving = true;
        upload = null; // a save that fails leaves nothing to wait for
        LongEventHandler.QueueLongEvent(() =>
        {
            try { GameDataSaveLoader.SaveGame(SaveFileName); }
            finally { IsSaving = false; }
        }, "Rimlink.Saving", doAsynchronously: false, ex => { IsSaving = false; Log.Error("[Rimlink] save failed: " + ex); });
        RimlinkLog.Instance.Info("saving (" + reason + ")");
        return true;
    }
}
