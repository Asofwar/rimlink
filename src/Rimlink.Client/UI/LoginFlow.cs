using Grpc.Core;
using RimWorld;
using Rimlink.Client.Game;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Rimlink.Client.Mods.Delivery;
using Rimlink.Protocol;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Main menu → server → login → modpack check → load the server save or start a new colony in the shared world.</summary>
public static class LoginFlow
{
    /// <summary>
    /// Main thread. The UI ignores the result; dev tooling passes preset credentials and pin answers and awaits where the
    /// flow ended. A preset login that ran out of attempts is <see cref="LoginOutcomeKind.Failed"/> with the server's reason.
    /// </summary>
    public static Task<LoginOutcome> Run(ServerEndpoint endpoint, GameCredentials? credentials = null, IPinPrompt? pins = null, bool fromRestart = false)
    {
        if (ModpackDownloadFlow.JoinBlocker() is { } blocker)
        {
            string error = (blocker switch { "Busy" => "Rimlink.Delivery.Error.Busy", "RestartRequired" => "Rimlink.Delivery.Error.RestartRequired", _ => "Rimlink.Delivery.Error.RecoveryRequired" }).Translate();
            Find.WindowStack.Add(new Dialog_MessageBox(error));
            return Task.FromResult(new LoginOutcome(LoginOutcomeKind.Failed, error));
        }
        RimlinkConnection.Pins = new CertificatePinStore(Path.Combine(RimlinkSession.DataDir, "pins.json"));
        var cts = new CancellationTokenSource();
        var wait = new Dialog_Wait("Rimlink.Flow.Connecting".Translate(endpoint.Display), cts);
        Find.WindowStack.Add(wait);
        credentials ??= new GameCredentials();
        pins ??= new GamePinPrompt();

        return Task.Run<LoginOutcome>(async () =>
        {
            RimlinkConnection? connection = null;
            try
            {
                connection = new RimlinkConnection(endpoint, credentials, pins, RimlinkLog.Instance);
                var reply = await Login(connection, credentials, endpoint, wait, cts.Token);

                if (reply.SessionToken.Length == 0)
                {
                    var diff = reply.ModpackDiff;
                    await connection.DisposeAsync();
                    string? login = credentials.LastPassword?.Login;
                    MainThread.Post(() => { wait.Close(); Find.WindowStack.Add(new Dialog_ModpackDiff(diff, endpoint, login)); });
                    return new LoginOutcome(LoginOutcomeKind.ModpackDiff);
                }

                if (reply.RecoveryVersion != SaveRecoveryData.CurrentVersion)
                    throw new IOException(await MainThread.Get(() => "Rimlink.Refusal.Recovery.ClientRequired".Translate().ToString()));

                bool deliveryVerified = await ModpackDownloadFlow.VerifyLogin(endpoint);
                if (!ModpackJoinReadiness.MayContinue(deliveryVerified, reply.Role, fromRestart))
                    throw new IOException(await MainThread.Get(() => "Rimlink.Delivery.Error.InstalledMismatch".Translate().ToString()));
                if (!deliveryVerified) RimlinkLog.Instance.Warn("manual admin login: modpack delivery remains unverified; backups retained");
                await MainThread.Get(() => { RimlinkSession.Begin(endpoint, connection, reply); return true; });
                var outbox = RimlinkSession.Outbox!;
                outbox.QuarantineOutsideScope(reply.SaveGeneration, reply.SaveWriteEpoch);
                if (reply.OutpostVersion == 0 && outbox.HasUnresolved)
                    throw new IOException(await MainThread.Get(() => "Rimlink.Refusal.Recovery.Required".Translate().ToString()));
                MainThread.Post(DevModeGuard.OnSessionBegan);
                MainThread.Post(() => wait.Text = "Rimlink.Flow.LoadingWorld".Translate());
                var world = await connection.ForAuthentication(reply,
                    () => connection.World.GetAsync(new Empty(), cancellationToken: cts.Token).ResponseAsync);
                OutpostScopeMessage? outpostScope = null;
                if (reply.OutpostVersion == OutpostRecoveryData.CurrentVersion)
                {
                    RequireCurrentLogin();
                    if (!world.Exists) throw new InvalidDataException("outpost world context is unavailable");
                    outpostScope = new OutpostScopeMessage { WorldInstanceId = world.WorldInstanceId, OwnerPlayerId = reply.Player.PlayerId,
                        SaveGeneration = reply.SaveGeneration, SaveWriteEpoch = reply.SaveWriteEpoch, RestoreBoundary = reply.OutpostRestoreBoundary };
                    outbox.QuarantineOutsideScope(outpostScope);
                }
                if (outbox.HasUnresolved)
                    throw new IOException(await MainThread.Get(() => "Rimlink.Refusal.Recovery.Required".Translate().ToString()));
                var objects = await connection.World.ListObjectsAsync(new Empty(), cancellationToken: cts.Token);
                var history = await connection.Chat.HistoryAsync(new Empty(), cancellationToken: cts.Token);
                await MainThread.Get(() =>
                {
                    RimlinkSession.World = world;
                    RimlinkSession.State.LoadSnapshot(objects, history, reply.Online.Count > 0 ? reply.Online : [reply.Player]);
                    ForeignSettlements.Subscribe(); return true;
                });

                string savePath = SaveSync.SaveFilePath;
                var pending = SaveSync.Pending;
                RimlinkSession.SummaryPending = true;
                var restore = reply.ColonyRestore;
                string? resetSave = null;
                bool keptForRollback = false;
                if (restore is { Restored: false })
                {
                    // The old colony stays in the server's archive: its local copy must not be uploaded as the new colony's save.
                    if (File.Exists(savePath)) pending.KeepLocalSave(savePath, "archived");
                    pending.Clear();
                    RimlinkLog.Instance.Warn($"archived colony '{restore.Name}' not restored ({restore.Reason}): starting a new colony");
                }
                // Only recovery candidates need parsing: an unused (possibly damaged) cache is replaced by the server copy.
                var localSnapshot = pending.RecoverSnapshot(savePath);
                string candidate = localSnapshot?.Path ?? savePath;
                var localMeta = File.Exists(candidate) && (pending.IsSet || !reply.HasSave) ? SaveMetaReader.Read(candidate) : null;
                if (localMeta is not null && (localMeta.SaveGeneration != reply.SaveGeneration || (localMeta.Recovery?.WriteEpoch ?? 0) != reply.SaveWriteEpoch))
                {
                    keptForRollback = localMeta.SaveGeneration == reply.SaveGeneration;
                    resetSave = Path.GetFileName(pending.KeepLocalSave(candidate, localMeta.SaveGeneration != reply.SaveGeneration ? "reset" : "rollback"));
                    RimlinkLog.Instance.Warn($"local save generation {localMeta.SaveGeneration} differs from server generation {reply.SaveGeneration}; kept as {resetSave}");
                    localSnapshot = null; localMeta = null; candidate = savePath;
                }
                else if (outpostScope is not null && localSnapshot is not null && reply.HasSave)
                {
                    RequireCurrentLogin();
                    if (pending.QuarantineOutsideRestoreBoundary(localSnapshot, outpostScope) is { } kept)
                    {
                        RimlinkLog.Instance.Warn($"pending outpost save from the previous restore boundary kept as {Path.GetFileName(kept)}");
                        localSnapshot = null; localMeta = null; candidate = savePath;
                    }
                }
                var action = LoginPlan.Decide(pending.IsSet, localMeta is not null && File.Exists(candidate), reply.HasSave, reply.HasSettlement,
                    localMeta?.SaveGeneration ?? 0, reply.SaveGeneration);
                RimlinkLog.Instance.Info($"login plan: {action} (pending={pending.IsSet}, local={File.Exists(savePath)}, server save={reply.HasSave}, settlement={reply.HasSettlement})");

                switch (action)
                {
                    case LoginAction.UploadLocalThenLoad:
                        MainThread.Post(() => wait.Text = "Rimlink.Flow.Uploading".Translate());
                        if (localSnapshot is null) { pending.Set(); localSnapshot = pending.RecoverSnapshot(savePath)!; }
                        RimlinkSession.Outbox.ReconcileSnapshot(localSnapshot.Meta.Recovery ?? new SaveRecoveryData { Generation = localSnapshot.Meta.SaveGeneration },
                            accepted: false, selected: true);
                        if (RimlinkSession.Outbox.HasUnresolved) throw new IOException(await MainThread.Get(() => "Rimlink.Refusal.Recovery.Required".Translate().ToString()));
                        var recovered = await UploadLocal(connection, savePath, pending, localSnapshot, cts.Token);
                        resetSave ??= recovered.GenerationSave;
                        if (recovered.Uploaded)
                        {
                            await RecoverSelection(connection, savePath, cts.Token);
                            MainThread.Post(() => { wait.Close(); GameDataSaveLoader.LoadGame(SaveSync.SaveFileName); });
                            return new LoginOutcome(LoginOutcomeKind.Loading);
                        }
                        // Refused: the file was set aside; carry on with whatever the server has.
                        if (reply.HasSave) goto case LoginAction.DownloadThenLoad;
                        if (reply.HasSettlement) goto case LoginAction.AbandonThenNewColony;
                        goto case LoginAction.NewColony;

                    case LoginAction.DownloadThenLoad:
                        MainThread.Post(() => wait.Text = "Rimlink.Flow.Downloading".Translate());
                        RequireCurrentLogin();
                        // A held operation may still need this cache as evidence; downloading must not erase it.
                        if (File.Exists(savePath) && outbox.Entries.Any(e => e.SnapshotId is not null || e.Phase is Rimlink.Client.Economy.OutboxPhase.Prepared or Rimlink.Client.Economy.OutboxPhase.Returning))
                            RimlinkLog.Instance.Warn("recovery cache kept as " + pending.KeepLocalSave(savePath, "recovery"));
                        var downloaded = await connection.ForAuthentication(reply,
                            () => new SaveTransfer(connection).DownloadSnapshotAsync(savePath, cts.Token))
                            ?? throw new IOException("the authoritative save is no longer available");
                        RequireCurrentLogin();
                        await MainThread.Get(() => { RequireCurrentLogin(); RimlinkSession.PendingDownload = downloaded; return true; });
                        pending.Clear();
                        await RecoverSelection(connection, savePath, cts.Token);
                        MainThread.Post(() =>
                        {
                            wait.Close();
                            ContinueAfterReset(resetSave, () => GameDataSaveLoader.LoadGame(SaveSync.SaveFileName), keptForRollback);
                        });
                        return new LoginOutcome(LoginOutcomeKind.Loading);

                    case LoginAction.AbandonThenNewColony:
                        // A settlement without a save anywhere (the first save never happened): the colony is gone, free its tile.
                        foreach (var own in RimlinkSession.State.Objects.All.Where(o => o.Owner?.PlayerId == RimlinkSession.MyPlayerId).ToList())
                        {
                            await connection.World.AbandonAsync(new AbandonRequest { ObjectId = own.Id }, cancellationToken: cts.Token);
                            RimlinkSession.State.Objects.Remove(own.Id);
                            RimlinkLog.Instance.Warn($"abandoned settlement {own.Id} on tile {own.Tile}: no save anywhere");
                        }
                        goto case LoginAction.NewColony;

                    case LoginAction.NewColony:
                        if (RimlinkSession.Outbox.Entries.Any(e => e.Phase is Rimlink.Client.Economy.OutboxPhase.Prepared or Rimlink.Client.Economy.OutboxPhase.Returning))
                            throw new IOException(await MainThread.Get(() => "Rimlink.Refusal.Recovery.Required".Translate().ToString()));
                        await RimlinkSession.Recovery!.ReplayReadyAsync(cts.Token);
                        await LoadEconomy(connection, RimlinkSession.Outbox, reply.Player.PlayerId, cts.Token);
                        // The vanilla new-colony pages, with the world page locked to the server's planet.
                        MainThread.Post(() =>
                        {
                            wait.Close();
                            ContinueAfterReset(resetSave, () =>
                            {
                                if (restore is { Restored: false })
                                    Find.WindowStack.Add(new Dialog_MessageBox(
                                        (restore.Reason == "damaged" ? "Rimlink.Archive.Damaged" : "Rimlink.Archive.TileTaken").Translate(restore.Name),
                                        buttonAAction: () => Find.WindowStack.Add(new Page_SelectScenario())));
                                else
                                    Find.WindowStack.Add(new Page_SelectScenario());
                            }, keptForRollback);
                        });
                        return new LoginOutcome(LoginOutcomeKind.NewColony);
                }
                void RequireCurrentLogin()
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(RimlinkSession.Connection, connection) || !ReferenceEquals(connection.Login, reply))
                        throw new OperationCanceledException("save recovery belongs to a previous login");
                }
                throw new InvalidOperationException($"unknown login action {action}");
            }
            catch (OperationCanceledException)
            {
                await Fail(connection, wait, null);
                return Cancelled(credentials);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cts.IsCancellationRequested)
            {
                await Fail(connection, wait, null);
                return Cancelled(credentials);
            }
            catch (RpcException ex)
            {
                string error = await ErrorMessage(ex);
                await Fail(connection, wait, error);
                return new LoginOutcome(LoginOutcomeKind.Failed, error);
            }
            catch (Exception ex)
            {
                await Fail(connection, wait, ex.Message);
                return new LoginOutcome(LoginOutcomeKind.Failed, ex.Message);
            }
        });
    }

    /// <summary>A preset login stops by "cancelling" once its attempts run out: that is a failure with the last refusal.</summary>
    private static LoginOutcome Cancelled(GameCredentials credentials) =>
        credentials.Interactive || credentials.LastError is null
            ? new LoginOutcome(LoginOutcomeKind.Cancelled)
            : new LoginOutcome(LoginOutcomeKind.Failed, credentials.LastError);

    /// <summary>Connects, re-asking for credentials where a retry can help; everything else propagates.</summary>
    private static async Task<LoginReply> Login(RimlinkConnection connection, GameCredentials credentials, ServerEndpoint endpoint, Dialog_Wait wait, CancellationToken ct)
    {
        bool serverPasswordAsked = false;
        var steamWait = new SteamLoginWait();
        while (true)
        {
            try
            {
                return await connection.ConnectAsync(ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated && !credentials.PreferPassword
                                          && steamWait.Next(ErrorCodeOf(ex)) is { } delay)
            {
                // Steam did not accept the ticket; it may take a couple of minutes, and a new ticket goes with the next try.
                RimlinkLog.Instance.Info($"Steam refused the ticket, trying again in {delay.TotalSeconds:0} s");
                MainThread.Post(() => wait.Text = "Rimlink.Flow.SteamWait".Translate());
                await Task.Delay(delay, ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated && !credentials.PreferPassword)
            {
                // The Steam ticket was refused (server without a Steam key, or Steam offline): offer the account login.
                credentials.PreferPassword = true;
                credentials.LastError = await ErrorMessage(ex);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.AlreadyExists or StatusCode.InvalidArgument
                                          && credentials.LastPassword is not null)
            {
                // Wrong password or a refused registration (name taken, bad name): ask again instead of failing the whole flow.
                credentials.LastError = await ErrorMessage(ex);
                credentials.ForgetPassword();
            }
            catch (RpcException ex) when (ErrorCodeOf(ex) == ErrorCode.WrongPassword && !serverPasswordAsked && credentials.Interactive)
            {
                // The server has a join password: ask once; a second refusal is reported like any other error.
                serverPasswordAsked = true;
                string? serverPassword = await MainThread.Ask<string?>(tcs => new Dialog_ServerPassword(endpoint.Display, tcs));
                if (serverPassword is null) throw new OperationCanceledException("join cancelled");
                credentials.ServerPassword = serverPassword;
            }
        }
    }

    /// <summary>
    /// Sends the local file the server never got. A permanent refusal keeps it separately; temporary failures retain recovery.
    /// </summary>
    private static async Task<(bool Uploaded, string? GenerationSave)> UploadLocal(RimlinkConnection connection, string savePath, PendingUpload pending, SaveSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var info = await new SaveTransfer(connection).UploadAsync(snapshot.Path, snapshot.Meta, ct);
            if (!info.IsCurrent)
            {
                pending.KeepLocalSave(snapshot.Path, "superseded");
                return (false, null);
            }
            string temp = savePath + ".recover-" + Guid.NewGuid().ToString("N");
            try { File.Copy(snapshot.Path, temp); File.Move(temp, savePath, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            RimlinkSession.Outbox!.ReconcileSnapshot(snapshot.Meta.Recovery ?? new SaveRecoveryData { Generation = snapshot.Meta.SaveGeneration }, accepted: true, snapshot.Meta.GameTick, selected: true);
            pending.CompleteSnapshot(snapshot);
            RimlinkLog.Instance.Info("pending local save uploaded");
            return (true, null);
        }
        catch (RpcException ex) when (LoginPlan.IsPermanentUploadRefusal(ex))
        {
            bool stale = Rimlink.Client.Economy.EconomyClient.CodeOf(ex) == ErrorCode.SaveStale;
            bool reset = Rimlink.Client.Economy.EconomyClient.CodeOf(ex) == ErrorCode.SaveGenerationMismatch;
            string kept = pending.KeepLocalSave(snapshot.Path, reset ? "reset" : stale ? "stale" : "rejected");
            string reason = await ErrorMessage(ex);
            RimlinkLog.Instance.Warn($"pending local save refused ({reason}); kept as {kept}");
            if (!reset)
                MainThread.Post(() => Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Save.Rejected".Translate(reason, Path.GetFileName(kept)))));
            return (false, reset ? Path.GetFileName(kept) : null);
        }
    }

    private static async Task RecoverSelection(RimlinkConnection connection, string path, CancellationToken ct)
    {
        var meta = SaveMetaReader.Read(path);
        var data = meta.Recovery ?? new SaveRecoveryData { Generation = meta.SaveGeneration };
        RimlinkSession.Outbox!.ReconcileSelectedSnapshot(data, meta.GameTick, RimlinkSession.PendingDownload?.Header);
        if (RimlinkSession.Outbox.HasUnresolved) throw new IOException(await MainThread.Get(() => "Rimlink.Refusal.Recovery.Required".Translate().ToString()));
        var replay = await RimlinkSession.Recovery!.ReplayReadyAsync(ct);
        await LoadEconomy(connection, RimlinkSession.Outbox, connection.Login!.Player.PlayerId, ct);
        await MainThread.Get(() => { RimlinkSession.RecoveryNeeded |= replay.Pending > 0; return true; });
    }

    private static void ContinueAfterReset(string? kept, Action next, bool rollback = false)
    {
        if (kept is null) next();
        else Find.WindowStack.Add(new Dialog_MessageBox((rollback ? "Rimlink.Save.RollbackLocalKept" : "Rimlink.Save.GenerationMismatch").Translate(kept), buttonAAction: next));
    }

    private static async Task Fail(RimlinkConnection? connection, Dialog_Wait wait, string? error)
    {
        await MainThread.Get(() => { if (RimlinkSession.Connection == connection) RimlinkSession.End(); return true; });
        if (connection is not null) await connection.DisposeAsync();
        MainThread.Post(() =>
        {
            wait.Close();
            if (error is not null) Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Flow.Error".Translate(error)));
        });
    }

    /// <summary>
    /// Refreshes independent recovery states after the selected snapshot has been reconciled and ready requests replayed.
    /// A frozen wallet cannot prevent mailbox, raids or terminal live sessions from being recovered.
    /// </summary>
    public static async Task LoadEconomy(RimlinkConnection connection, Rimlink.Client.Economy.TransferOutbox? outbox, long me, CancellationToken ct)
    {
        if (RimlinkSession.Connection != connection || RimlinkSession.Recovery is not { } recovery) return;
        bool complete = await recovery.LoadStatesAsync(ct);
        await MainThread.Get(() => { if (RimlinkSession.Connection == connection) RimlinkSession.RecoveryNeeded |= !complete; return true; });
    }

    private static ErrorCode? ErrorCodeOf(RpcException ex) => Rimlink.Client.Economy.EconomyClient.CodeOf(ex);

    /// <summary>The refusal in the player's language: read here, translated on the main thread (the login runs in the background).</summary>
    private static Task<string> ErrorMessage(RpcException ex)
    {
        var refusal = RpcErrorDetail.Read(ex);
        return MainThread.Get(() => RefusalText.Of(refusal));
    }
}
