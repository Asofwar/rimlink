using Rimlink.Client.Net;
using Rimlink.Client.State;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>The one multiplayer session of this game process. Null connection = single player as usual.</summary>
public static class RimlinkSession
{
    public static RimlinkConnection? Connection { get; private set; }
    public static ServerEndpoint? Endpoint { get; private set; }
    public static LoginReply? Login { get; private set; }
    public static WorldInfo? World { get; set; }
    public static SharedState State { get; } = new();
    public static EventInbox Inbox { get; } = new();
    public static ConnectionState LastState { get; set; } = ConnectionState.Disconnected;

    public static bool Active => Connection is not null && Login is not null && Login.SessionToken.Length > 0;
    public static Rimlink.Client.Economy.EconomyClient? Economy => Connection is null ? null : new(Connection);
    /// <summary>Economy requests whose answer has not been seen; replayed at login and after a reconnect.</summary>
    public static Rimlink.Client.Economy.TransferOutbox? Outbox { get; private set; }
    public static long MyPlayerId => Login?.Player.PlayerId ?? 0;
    public static bool IsAdmin => Login?.Role == Role.Admin;
    public static bool IsModerator => Login?.Role is Role.Admin or Role.Moderator;

    /// <summary>Per-server data under the game's config folder: pins, saves, pending flag.</summary>
    public static string DataDir => Path.Combine(GenFilePaths.SaveDataFolderPath, "Rimlink");

    public static string SaveDir(ServerEndpoint endpoint, long playerId) => Path.Combine(GenFilePaths.SavedGamesFolderPath, "Rimlink", $"{endpoint.Id}_{playerId}");
    public static string OutboxPath(ServerEndpoint endpoint, long playerId) => Path.Combine(SaveDir(endpoint, playerId), "outbox.json");

    public static void Begin(ServerEndpoint endpoint, RimlinkConnection connection, LoginReply login)
    {
        Endpoint = endpoint;
        Connection = connection;
        Login = login;
        Outbox = new(OutboxPath(endpoint, login.Player.PlayerId), RimlinkLog.Instance);
        State.Reset();
        Inbox.Clear();
        connection.EventReceived += Inbox.Enqueue;
        connection.StateChanged += s => LastState = s;
        LastState = connection.State;
    }

    /// <summary>Forgets the session; the connection is disposed by the caller.</summary>
    public static void End()
    {
        if (Connection is { } c) c.EventReceived -= Inbox.Enqueue;
        Connection = null;
        Login = null;
        Outbox = null;
        Endpoint = null;
        World = null;
        State.Reset();
        Inbox.Clear();
        LastState = ConnectionState.Disconnected;
    }
}
