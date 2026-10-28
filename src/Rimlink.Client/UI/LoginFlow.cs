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
    public static void Run(ServerEndpoint endpoint)
    {
        RimlinkConnection.Pins = new CertificatePinStore(Path.Combine(RimlinkSession.DataDir, "pins.json"));
        var cts = new CancellationTokenSource();
        var wait = new Dialog_Wait("Rimlink.Flow.Connecting".Translate(endpoint.Display), cts);
        Find.WindowStack.Add(wait);
        var credentials = new GameCredentials();

        _ = Task.Run(async () =>
        {
            RimlinkConnection? connection = null;
            try
            {
                connection = new RimlinkConnection(endpoint, credentials, new GamePinPrompt(), RimlinkLog.Instance);
                var reply = await Login(connection, credentials, endpoint, cts.Token);

                if (reply.SessionToken.Length == 0)
                {
                    var diff = reply.ModpackDiff;
                    await connection.DisposeAsync();
                    MainThread.Post(() => { wait.Close(); Find.WindowStack.Add(new Dialog_ModpackDiff(diff)); });
                    return;
                }

                RimlinkSession.Begin(endpoint, connection, reply);
                MainThread.Post(() => wait.Text = "Rimlink.Flow.LoadingWorld".Translate());
                var world = await connection.World.GetAsync(new Empty(), cancellationToken: cts.Token);
                var objects = await connection.World.ListObjectsAsync(new Empty(), cancellationToken: cts.Token);
                var history = await connection.Chat.HistoryAsync(new Empty(), cancellationToken: cts.Token);
                RimlinkSession.World = world;
                RimlinkSession.State.LoadSnapshot(objects, history, reply.Online.Count > 0 ? reply.Online : [reply.Player]);
                ForeignSettlements.Subscribe();

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
                            return;
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
                        return;

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
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                await Fail(connection, wait, null);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cts.IsCancellationRequested)
            {
                await Fail(connection, wait, null);
            }
            catch (RpcException ex)
            {
                await Fail(connection, wait, ErrorMessage(ex));
            }
            catch (Exception ex)
            {
                await Fail(connection, wait, ex.Message);
            }
        });
    }

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
                credentials.LastError = ex.Status.Detail;
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.AlreadyExists or StatusCode.InvalidArgument
                                          && credentials.LastPassword is not null)
            {
                // Wrong password or a refused registration (name taken, bad name): ask again instead of failing the whole flow.
                credentials.LastError = ex.Status.Detail;
                credentials.ForgetPassword();
            }
            catch (RpcException ex) when (ErrorCodeOf(ex) == ErrorCode.WrongPassword && !serverPasswordAsked)
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
            var meta = ReadMeta(savePath);
            await new SaveTransfer(connection).UploadAsync(savePath, meta, ct);
            pending.Clear();
            RimlinkLog.Instance.Info("pending local save uploaded");
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled && ex.StatusCode != StatusCode.Unavailable)
        {
            string kept = Path.ChangeExtension(savePath, $".rejected-{DateTime.Now:yyyyMMdd-HHmm}.rws");
            File.Move(savePath, kept, overwrite: true);
            pending.Clear();
            string reason = ErrorMessage(ex);
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

    private static ErrorCode? ErrorCodeOf(RpcException ex) =>
        ex.Trailers.FirstOrDefault(t => t.Key == "rimlink-error-bin") is { } trailer ? ErrorDetail.Parser.ParseFrom(trailer.ValueBytes).Code : null;

    private static string ErrorMessage(RpcException ex) =>
        ex.Trailers.FirstOrDefault(t => t.Key == "rimlink-error-bin") is { } trailer
            ? ErrorDetail.Parser.ParseFrom(trailer.ValueBytes).Message
            : $"{ex.StatusCode}: {ex.Status.Detail}";

    /// <summary>Seed, faction name and tick straight from the .rws header, without loading the game.</summary>
    private static SaveMeta ReadMeta(string rwsPath)
    {
        string seed = "", faction = "";
        long tick = 0;
        using var reader = System.Xml.XmlReader.Create(rwsPath, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit });
        string? pendingDef = null;
        bool advance = true;
        while (!advance || reader.Read())
        {
            advance = true;
            if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
            switch (reader.Name)
            {
                case "seedString" when seed.Length == 0: seed = reader.ReadElementContentAsString(); advance = false; break;
                case "ticksGame" when tick == 0: tick = long.Parse(reader.ReadElementContentAsString()); advance = false; break;
                case "def" when faction.Length == 0: pendingDef = reader.ReadElementContentAsString(); advance = false; break;
                case "name" when faction.Length == 0 && pendingDef is "PlayerColony" or "PlayerTribe": faction = reader.ReadElementContentAsString(); advance = false; break;
            }
            if (seed.Length > 0 && faction.Length > 0 && tick > 0) break;
        }
        return new SaveMeta(seed, faction, tick);
    }
}
