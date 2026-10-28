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
                LoginReply reply;
                while (true)
                {
                    try
                    {
                        reply = await connection.ConnectAsync(cts.Token);
                        break;
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
                }

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
                // Only this player is known to be online at login; others show up through PlayerJoined events.
                RimlinkSession.State.LoadSnapshot(objects, history, [reply.Player]);
                ForeignSettlements.Subscribe();

                if (reply.HasSave)
                {
                    MainThread.Post(() => wait.Text = "Rimlink.Flow.Downloading".Translate());
                    await new SaveTransfer(connection).DownloadAsync(SaveSync.SaveFilePath, cts.Token);
                    SaveSync.Pending.Clear();
                    MainThread.Post(() => { wait.Close(); GameDataSaveLoader.LoadGame(SaveSync.SaveFileName); });
                    return;
                }

                if (SaveSync.Pending.IsSet && File.Exists(SaveSync.SaveFilePath))
                {
                    // The last session's save never reached the server; it is the newest copy there is.
                    MainThread.Post(() => wait.Text = "Rimlink.Flow.Uploading".Translate());
                    var meta = ReadMeta(SaveSync.SaveFilePath);
                    await new SaveTransfer(connection).UploadAsync(SaveSync.SaveFilePath, meta, cts.Token);
                    SaveSync.Pending.Clear();
                    MainThread.Post(() => { wait.Close(); GameDataSaveLoader.LoadGame(SaveSync.SaveFileName); });
                    return;
                }

                if (reply.HasSettlement)
                {
                    // A settlement without a save (the first save never happened): the colony is gone, free its tile.
                    foreach (var own in RimlinkSession.State.Objects.All.Where(o => o.Owner?.PlayerId == RimlinkSession.MyPlayerId).ToList())
                    {
                        await connection.World.AbandonAsync(new AbandonRequest { ObjectId = own.Id }, cancellationToken: cts.Token);
                        RimlinkSession.State.Objects.Remove(own.Id);
                        RimlinkLog.Instance.Warn($"abandoned settlement {own.Id} on tile {own.Tile}: no save on the server");
                    }
                }

                // No save yet: the vanilla new-colony pages, with the world page locked to the server's planet.
                MainThread.Post(() => { wait.Close(); Find.WindowStack.Add(new Page_SelectScenario()); });
            }
            catch (OperationCanceledException)
            {
                await Fail(connection, wait, null);
            }
            catch (RpcException ex)
            {
                await Fail(connection, wait, ex.Trailers.FirstOrDefault(t => t.Key == "rimlink-error-bin") is { } trailer
                    ? ErrorDetail.Parser.ParseFrom(trailer.ValueBytes).Message
                    : $"{ex.StatusCode}: {ex.Status.Detail}");
            }
            catch (Exception ex)
            {
                await Fail(connection, wait, ex.Message);
            }
        });
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
