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
    /// <summary>Why the last finished upload failed, null after a success; a server refusal is translated when read on the main thread.</summary>
    public static string? LastError => lastRefusal is { } refusal ? Rimlink.Client.UI.RefusalText.Of(refusal) : lastFailure;
    private static Refusal? lastRefusal;
    private static string? lastFailure;
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
            string path = SaveFilePath;
            var before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            GameDataSaveLoader.SaveGame(SaveFileName); // the postfix starts an upload that fails or is cancelled; the flag below covers both
            // SaveGame logs and swallows its own exceptions: only a rewritten file counts as saved.
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) == before)
            {
                RimlinkLog.Instance.Warn($"local save failed ({reason}): the file was not written");
                return false;
            }
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
        var game = Current.Game;
        var component = game.GetComponent<RimlinkGameComponent>();
        var meta = new SaveMeta(Find.World.info.seedString, Faction.OfPlayer.Name, Find.TickManager.TicksGame,
            (long)(home?.wealthWatcher.WealthTotal ?? 0f), (long)(home?.wealthWatcher.WealthItems ?? 0f), component?.DevModeSinceUpload ?? false);
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
                lastRefusal = null;
                lastFailure = null;
                LastVersion = info.Version;
                RimlinkLog.Instance.Info($"save uploaded as v{info.Version}{(meta.DevMode ? " (dev mode flagged)" : "")}");
                // The server has the flag; the next upload reports only what happens from now on.
                if (meta.DevMode) UI.MainThread.Post(() => { if (Current.Game == game && component is not null) component.DevModeSinceUpload = false; });
                if (shopList is not null) await Shop.ShopSync.Publish(shopList, connection);
            }
            catch (Grpc.Core.RpcException ex) when (Rimlink.Client.Economy.EconomyClient.CodeOf(ex) == Rimlink.Protocol.V1.ErrorCode.SaveStale)
            {
                // Not a retry case: this file would bring back what was given away. The player decides in game.
                var refusal = RpcErrorDetail.Read(ex);
                lastFailure = null;
                lastRefusal = refusal;
                StaleSaveDetected = true;
                RimlinkLog.Instance.Warn($"save refused as stale: {refusal.Message}");
            }
            catch (Exception ex)
            {
                pending.Set();
                lastRefusal = null;
                lastFailure = ex.Message;
                RimlinkLog.Instance.Warn($"save upload failed, will retry at next login: {ex.Message}");
            }
        });
    }

    private static Action? leaveThen;

    /// <summary>A leave flow (<see cref="SaveUploadAndEnd"/>) is running: the session ends once its upload is done or timed out.</summary>
    public static bool Leaving => leaveThen is not null;

    /// <summary>The own colony is loaded and can be saved (not a visit's copy of another colony, not a menu or new-colony page).</summary>
    public static bool HasColonyToSave => Current.Game != null && Current.ProgramState == ProgramState.Playing && !Visits.VisitSession.InVisit;

    /// <summary>
    /// Leaving the session (to the menu, to the OS): saves the colony, waits up to 60 s for the upload behind a long event, flags
    /// the file for the next login when it did not finish, ends the session, then runs <paramref name="then"/>. Called while a
    /// flow runs, it only replaces what runs at the end (a quit during leave-to-menu quits). With nothing to save it ends the
    /// session at once.
    /// </summary>
    public static void SaveUploadAndEnd(Action then)
    {
        if (leaveThen is not null) { leaveThen = then; return; }
        leaveThen = then;
        // In a visit the loaded game is the host's copy: nothing to save, the own colony was uploaded when the visit began.
        if (!HasColonyToSave) { FinishLeaving(); return; }
        upload = null; // a save that fails leaves nothing to wait for (not the previous upload)
        try { GameDataSaveLoader.SaveGame(SaveFileName); } // the SaveGame postfix starts the upload
        catch (Exception ex) { Log.Error("[Rimlink] save before leaving failed: " + ex); }
        var pending = Pending; // while the session (and its endpoint) still exists
        bool uploaded = false;
        LongEventHandler.QueueLongEvent(() => uploaded = WaitForUpload(TimeSpan.FromSeconds(60)), "Rimlink.Flow.Uploading",
            doAsynchronously: true, null, showExtraUIInfo: false, callback: () =>
            {
                // An upload that did not finish in time is not trusted: the local file is flagged and sent at the next login.
                if (!uploaded) pending.Set();
                FinishLeaving();
            });
    }

    private static void FinishLeaving()
    {
        RimlinkGameComponent.EndSession();
        var next = leaveThen;
        leaveThen = null;
        next?.Invoke();
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
