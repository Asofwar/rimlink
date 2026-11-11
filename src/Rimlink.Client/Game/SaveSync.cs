using RimWorld;
using Rimlink.Client.Net;
using Verse;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

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

    public sealed record UploadResult(SaveSnapshot? Snapshot, SaveInfo? Info, Exception? Error)
    {
        public bool Uploaded => Info is not null && Error is null;
    }
    private static Task<UploadResult>? upload;
    private static Action<SaveSnapshot>? beforeExactUpload;
    private static Outposts.OutpostAcceptanceContext? exactAcceptance;
    private static string? exactStage;

    /// <summary>Main-thread file capture; durable journal callback runs before any bytes can be uploaded.</summary>
    public static async Task<UploadResult> SaveExactAsync(Action<SaveSnapshot>? journal = null)
    {
        var waiting = await MainThread.Get(() => SaveExactNow(journal));
        return await waiting;
    }
    internal static async Task<UploadResult> SaveExactAsync(Action<SaveSnapshot>? journal, Outposts.OutpostAcceptanceContext? acceptance, string stage)
    {
        var waiting = await MainThread.Get(() => SaveExactNowCore(journal, acceptance, stage));
        return await waiting;
    }

    /// <summary>Main thread only: capture immediately, without an await gap after a protected departure.</summary>
    public static Task<UploadResult> SaveExactNow(Action<SaveSnapshot>? journal = null)
        => SaveExactNowCore(journal, null, null);
    private static Task<UploadResult> SaveExactNowCore(Action<SaveSnapshot>? journal, Outposts.OutpostAcceptanceContext? acceptance, string? stage)
    {
        if (!HasColonyToSave || IsSaving || writesBlocked || beforeExactUpload is not null)
            throw new InvalidOperationException("an exact own snapshot cannot be written now");
        IsSaving = true; upload = null; beforeExactUpload = journal; exactAcceptance = acceptance; exactStage = stage;
        try { GameDataSaveLoader.SaveGame(SaveFileName); }
        finally { beforeExactUpload = null; exactAcceptance = null; exactStage = null; IsSaving = false; }
        return upload ?? Task.FromResult(new UploadResult(null, null, new IOException("the exact snapshot was not written")));
    }
    private static SaveUploadQueue uploads = new();
    private static CancellationTokenSource uploadLifetime = new();
    private static RimlinkConnection? owner;
    private static bool writesBlocked;
    public static bool WritesBlocked => writesBlocked;
    public static bool UploadInFlight => upload is { IsCompleted: false };
    public static SaveSnapshot? LastSavedSnapshot { get; private set; }

    public static void Attach(RimlinkConnection connection)
    {
        uploadLifetime.Cancel(); uploadLifetime = new(); uploads = new(); owner = connection; writesBlocked = false;
        upload = null; LastSavedSnapshot = null; LastUploadUtc = null; LastVersion = 0; lastRefusal = null; lastFailure = null; StaleSaveDetected = false;
    }
    public static void Detach() { writesBlocked = true; owner = null; uploadLifetime.Cancel(); }
    public static void BlockWrites() { writesBlocked = true; uploadLifetime.Cancel(); }

    /// <summary>The durable intent must exist before vanilla can replace the own-colony file.</summary>
    public static bool BeforeSave(string fileName)
    {
        if (GuildCapture.BattleGameContext.Active) return GuildCapture.BattleGameContext.MaySave(fileName);
        if (GuildCapture.BattleExecutorStartup.Selected) return GuildCapture.BattleExecutorStartup.WritingTemplate;
        try
        {
            if (Outposts.PawnCustodyRegistry.IsOwnSave && Current.Game is { } own)
            {
                var evidence = own.GetComponent<RimlinkGameComponent>();
                var registry = own.GetComponent<Outposts.PawnCustodyRegistry>();
                var validation = registry.ValidateOwnGame();
                if (evidence.OutpostLoadFailure is not null || registry.HasRecords && !evidence.OutpostRecovery.Present
                    || validation.RecoveryRequired || evidence.OutpostRecovery.Metadata.Registry.Any(known =>
                        !validation.Registry.Any(current => current.PawnCustodyId == known.PawnCustodyId)))
                    throw new InvalidDataException("own pawn custody requires recovery before saving");
                evidence.OutpostRecovery.Validate();
            }
            if (!IsOurs(fileName) || Visits.VisitSession.Active) return true;
            if (writesBlocked) return false;
            var component = Current.Game.GetComponent<RimlinkGameComponent>();
            string id = Guid.NewGuid().ToString("N");
            Pending.RecoverSnapshot(SaveFilePath); // resolve a previous failed write without trusting its older cache
            Pending.BeginSnapshot(id, component.SaveGeneration, component.SaveWriteEpoch);
            component.SnapshotId = id; component.RecoveryVersion = SaveRecoveryData.CurrentVersion;
            return true;
        }
        catch (Exception ex)
        {
            lastFailure = ex.Message; lastRefusal = null; LastSavedSnapshot = null;
            upload = Task.FromResult(new UploadResult(null, null, ex));
            RimlinkLog.Instance.Warn("save preflight refused: " + ex.Message);
            return false;
        }
    }

    public static UploadResult WaitForSnapshot(TimeSpan timeout)
    {
        var waiting = upload;
        if (waiting is null) return new(null, null, new IOException("the game did not write a snapshot"));
        try { return waiting.Wait(timeout) ? waiting.GetAwaiter().GetResult() : new(LastSavedSnapshot, null, new TimeoutException("snapshot upload timed out")); }
        catch (Exception ex) { return new(LastSavedSnapshot, null, ex); }
    }

    public static bool IsOurs(string fileName) => !GuildCapture.BattleGameContext.Active && RimlinkSession.Active && fileName == SaveFileName;

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
        if (GuildCapture.BattleGameContext.Active || !RimlinkSession.Active || Current.Game == null || Current.ProgramState != ProgramState.Playing || Visits.VisitSession.InVisit) return false; // a visit's game is the host's copy; the own colony is already uploaded
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
        if (!IsOurs(fileName) || Find.World == null || Visits.VisitSession.Active || writesBlocked) return;
        var game = Current.Game;
        var component = game.GetComponent<RimlinkGameComponent>();
        var connection = RimlinkSession.Connection!;
        var authentication = connection.Login;
        var pending = Pending;
        SaveSnapshot snapshot;
        try { snapshot = pending.CaptureSnapshot(SaveFilePath); LastSavedSnapshot = snapshot; }
        catch (Exception ex)
        {
            try { pending.RecoverSnapshot(SaveFilePath); } catch { }
            lastFailure = ex.Message; lastRefusal = null; LastSavedSnapshot = null;
            upload = Task.FromResult(new UploadResult(null, null, ex));
            RimlinkLog.Instance.Warn("the prepared game snapshot was not written: " + ex.Message);
            return;
        }
        try { beforeExactUpload?.Invoke(snapshot); }
        catch (Exception ex)
        {
            lastFailure = ex.Message; lastRefusal = null;
            upload = Task.FromResult(new UploadResult(snapshot, null, ex));
            RimlinkLog.Instance.Warn("exact snapshot retained; journal was not durable: " + ex.Message);
            return; // Never upload a removal whose corresponding immutable intent could not be journaled.
        }
        var shopList = Shop.ShopSync.Build(snapshot.Meta.GameTick);
        var outbox = RimlinkSession.Outbox;
        var acceptance = exactAcceptance; var stage = exactStage;
        var task = uploads.Enqueue(snapshot, async (copy, cancellation) =>
        {
            await Outposts.OutpostAcceptance.ReachAsync(stage + "-local-durable", acceptance, copy.Id);
            cancellation.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            return await connection.ForAuthentication(authentication!, () => new SaveTransfer(connection).UploadAsync(copy.Path, copy.Meta, timeout.Token));
        }, uploadLifetime.Token);
        upload = Complete();
        async Task<UploadResult> Complete()
        {
            try
            {
                var info = await task;
                if (owner != connection || RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, authentication) || writesBlocked)
                    return new(snapshot, info, new OperationCanceledException("snapshot accepted under a previous login; recovery evidence retained"));
                outbox?.ReconcileSnapshot(snapshot.Meta, accepted: true);
                pending.CompleteSnapshot(snapshot);
                try { uploads.PruneSnapshots(pending, outbox?.Entries.Select(e => e.SnapshotId) ?? []); }
                catch (IOException ex) { RimlinkLog.Instance.Warn("stored snapshot; obsolete local copies retained: " + ex.Message); }
                if (LastSavedSnapshot?.Id == snapshot.Id)
                {
                    LastUploadUtc = DateTime.UtcNow; lastRefusal = null; lastFailure = null; LastVersion = info.Version;
                }
                RimlinkLog.Instance.Info($"snapshot {snapshot.Id} uploaded as v{info.Version}");
                if (snapshot.Meta.DevMode) MainThread.Post(() => { if (Current.Game == game && component is not null) component.DevModeSinceUpload = false; });
                MainThread.Post(() =>
                {
                    if (RimlinkSession.Connection == connection && ReferenceEquals(connection.Login, authentication))
                    { RimlinkSession.RecoveryNeedsSave = false; RimlinkSession.RecoveryNeeded = true; }
                });
                if (shopList is not null)
                    try { await Shop.ShopSync.Publish(shopList, connection); }
                    catch (Exception ex) { RimlinkLog.Instance.Warn("snapshot stored; shop publication will be refreshed: " + ex.Message); }
                return new(snapshot, info, null);
            }
            catch (Exception ex)
            {
                if (owner != connection || RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, authentication)) return new(snapshot, null, ex);
                var refusal = ex is Grpc.Core.RpcException rpc ? RpcErrorDetail.Read(rpc) : null;
                if (LastSavedSnapshot?.Id == snapshot.Id) { lastFailure = refusal is null ? ex.Message : null; lastRefusal = refusal; }
                if (refusal?.Code is ErrorCode.SaveGenerationMismatch or ErrorCode.SaveWriteEpochMismatch)
                {
                    BlockWrites();
                    MainThread.Post(() =>
                    {
                        if (Current.Game != game || RimlinkSession.Connection != connection) return;
                        Find.TickManager.Pause();
                        Find.WindowStack.Add(new Dialog_MessageBox(RefusalText.Of(refusal), "Rimlink.Net.ToMenu".Translate(), RimlinkGameComponent.AbortToMenu));
                    });
                }
                else if (refusal?.Code == ErrorCode.SaveStale) StaleSaveDetected = true;
                RimlinkLog.Instance.Warn($"snapshot {snapshot.Id} upload failed; immutable recovery retained: {ex.Message}");
                return new(snapshot, null, ex);
            }
        }
    }

    private static Action? leaveThen;

    /// <summary>A leave flow (<see cref="SaveUploadAndEnd"/>) is running: the session ends once its upload is done or timed out.</summary>
    public static bool Leaving => leaveThen is not null;

    /// <summary>The own colony is loaded and can be saved (not a visit's copy of another colony, not a menu or new-colony page).</summary>
    public static bool HasColonyToSave => !GuildCapture.BattleGameContext.Active && Current.Game != null && Current.ProgramState == ProgramState.Playing && !Visits.VisitSession.InVisit;

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
        if (GuildCapture.BattleGameContext.Active || !RimlinkSession.Active || Current.Game == null || IsSaving || Visits.VisitSession.InVisit || writesBlocked) return false;
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
