using Grpc.Core;
using RimWorld;
using Rimlink.Client.Game;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Main menu → server → login → modpack check → load the server save or start a new colony in the shared world.</summary>
public static class LoginFlow
{
    /// <summary>
    /// Main thread. The UI ignores the result; dev tooling passes preset credentials and pin answers and awaits where the
    /// flow ended. A preset login that ran out of attempts is <see cref="LoginOutcomeKind.Failed"/> with the server's reason.
    /// </summary>
    public static Task<LoginOutcome> Run(ServerEndpoint endpoint, GameCredentials? credentials = null, IPinPrompt? pins = null)
    {
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
                var reply = await Login(connection, credentials, endpoint, cts.Token);

                if (reply.SessionToken.Length == 0)
                {
                    var diff = reply.ModpackDiff;
                    await connection.DisposeAsync();
                    string? login = credentials.LastPassword?.Login;
                    MainThread.Post(() => { wait.Close(); Find.WindowStack.Add(new Dialog_ModpackDiff(diff, endpoint, login)); });
                    return new LoginOutcome(LoginOutcomeKind.ModpackDiff);
                }

                RimlinkSession.Begin(endpoint, connection, reply);
                MainThread.Post(DevModeGuard.OnSessionBegan);
                MainThread.Post(() => wait.Text = "Rimlink.Flow.LoadingWorld".Translate());
                var world = await connection.World.GetAsync(new Empty(), cancellationToken: cts.Token);
                var objects = await connection.World.ListObjectsAsync(new Empty(), cancellationToken: cts.Token);
                var history = await connection.Chat.HistoryAsync(new Empty(), cancellationToken: cts.Token);
                RimlinkSession.World = world;
                RimlinkSession.State.LoadSnapshot(objects, history, reply.Online.Count > 0 ? reply.Online : [reply.Player]);
                ForeignSettlements.Subscribe();
                await LoadEconomy(connection, RimlinkSession.Outbox, reply.Player.PlayerId, cts.Token);

                string savePath = SaveSync.SaveFilePath;
                var pending = SaveSync.Pending;
                var action = LoginPlan.Decide(pending.IsSet, File.Exists(savePath), reply.HasSave, reply.HasSettlement);
                RimlinkLog.Instance.Info($"login plan: {action} (pending={pending.IsSet}, local={File.Exists(savePath)}, server save={reply.HasSave}, settlement={reply.HasSettlement})");

                switch (action)
                {
                    case LoginAction.UploadLocalThenLoad:
                        MainThread.Post(() => wait.Text = "Rimlink.Flow.Uploading".Translate());
                        if (await UploadLocal(connection, savePath, pending, cts.Token))
                        {
                            MainThread.Post(() => { wait.Close(); GameDataSaveLoader.LoadGame(SaveSync.SaveFileName); });
                            return new LoginOutcome(LoginOutcomeKind.Loading);
                        }
                        // Refused: the file was set aside; carry on with whatever the server has.
                        if (reply.HasSave) goto case LoginAction.DownloadThenLoad;
                        if (reply.HasSettlement) goto case LoginAction.AbandonThenNewColony;
                        goto case LoginAction.NewColony;

                    case LoginAction.DownloadThenLoad:
                        MainThread.Post(() => wait.Text = "Rimlink.Flow.Downloading".Translate());
                        await new SaveTransfer(connection).DownloadAsync(savePath, cts.Token);
                        pending.Clear();
                        MainThread.Post(() => { wait.Close(); GameDataSaveLoader.LoadGame(SaveSync.SaveFileName); });
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
                        // The vanilla new-colony pages, with the world page locked to the server's planet.
                        MainThread.Post(() => { wait.Close(); Find.WindowStack.Add(new Page_SelectScenario()); });
                        return new LoginOutcome(LoginOutcomeKind.NewColony);
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
    private static async Task<LoginReply> Login(RimlinkConnection connection, GameCredentials credentials, ServerEndpoint endpoint, CancellationToken ct)
    {
        bool serverPasswordAsked = false;
        while (true)
        {
            try
            {
                return await connection.ConnectAsync(ct);
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
    /// Sends the local file the server never got. A refusal (another world after a recreate, a damaged file) keeps the
    /// file under a dated name so nothing is lost, clears the marker and returns false.
    /// </summary>
    private static async Task<bool> UploadLocal(RimlinkConnection connection, string savePath, PendingUpload pending, CancellationToken ct)
    {
        try
        {
            var meta = SaveMetaReader.Read(savePath);
            await new SaveTransfer(connection).UploadAsync(savePath, meta, ct);
            pending.Clear();
            RimlinkLog.Instance.Info("pending local save uploaded");
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled && ex.StatusCode != StatusCode.Unavailable)
        {
            bool stale = Rimlink.Client.Economy.EconomyClient.CodeOf(ex) == ErrorCode.SaveStale;
            string kept = Path.ChangeExtension(savePath, $".{(stale ? "stale" : "rejected")}-{DateTime.Now:yyyyMMdd-HHmm}.rws");
            File.Move(savePath, kept, overwrite: true);
            pending.Clear();
            string reason = await ErrorMessage(ex);
            RimlinkLog.Instance.Warn($"pending local save refused ({reason}); kept as {kept}");
            MainThread.Post(() => Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Save.Rejected".Translate(reason, Path.GetFileName(kept)))));
            return false;
        }
    }

    private static async Task Fail(RimlinkConnection? connection, Dialog_Wait wait, string? error)
    {
        RimlinkSession.End();
        if (connection is not null) await connection.DisposeAsync();
        MainThread.Post(() =>
        {
            wait.Close();
            if (error is not null) Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Flow.Error".Translate(error)));
        });
    }

    /// <summary>
    /// Unanswered requests first (the server settles them with the same keys), then wallet, mailbox and own lots.
    /// A server with the economy off or frozen, or any other refusal, leaves the state empty; the login goes on.
    /// </summary>
    public static async Task LoadEconomy(RimlinkConnection connection, Rimlink.Client.Economy.TransferOutbox? outbox, long me, CancellationToken ct)
    {
        var economy = new Rimlink.Client.Economy.EconomyClient(connection);
        var raids = new Rimlink.Client.Raids.RaidClient(connection);
        try
        {
            var live = RimlinkSession.Live ?? new Rimlink.Client.Live.LiveClient(connection, RimlinkLog.Instance);
            var trades = new Rimlink.Client.Social.TradesClient(connection);
            if (outbox is not null) await outbox.Replay(new Rimlink.Client.Economy.OutboxClients(economy, raids, new Rimlink.Client.Visits.ShopClient(connection), live, trades), ct);
            Rimlink.Protocol.V1.EconomyRules? rules = null;
            try { rules = await economy.Rules(ct); }
            catch (RpcException ex) { RimlinkLog.Instance.Warn("economy rules not loaded: " + Rimlink.Client.Economy.EconomyClient.MessageOf(ex)); }
            RimlinkSession.State.Economy.Load(await economy.Wallet(ct), await economy.Pending(ct), await economy.Lots(new MarketQuery { SellerPlayerId = me }, ct), rules);
            RimlinkSession.State.Raids.Load(await raids.Pending(ct), await raids.Mine(ct), await raids.Flags(ct));
            RimlinkSession.State.Social.Load(await trades.List(ct), await new Rimlink.Client.Social.SocialClient(connection).ListBlocks(ct));
            LiveSession? current = null;
            try { current = await live.Current(ct); }
            catch (RpcException) { }
            RimlinkSession.State.Live.Load(connection.Login?.PendingLiveEnds ?? [], current);
        }
        catch (RpcException ex)
        {
            RimlinkLog.Instance.Warn("economy not loaded, it stays empty until the next reconnect: " + Rimlink.Client.Economy.EconomyClient.MessageOf(ex));
        }
    }

    private static ErrorCode? ErrorCodeOf(RpcException ex) => Rimlink.Client.Economy.EconomyClient.CodeOf(ex);

    /// <summary>The refusal in the player's language: read here, translated on the main thread (the login runs in the background).</summary>
    private static Task<string> ErrorMessage(RpcException ex)
    {
        var refusal = RpcErrorDetail.Read(ex);
        return MainThread.Get(() => RefusalText.Of(refusal));
    }
}
