using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.State;

/// <summary>Local copy of everything shared. Mutated only from the thread that drains the <see cref="EventInbox"/> (the game's main thread).</summary>
public sealed class SharedState
{
    public WorldObjectsIndex Objects { get; } = new();
    public ChatLog Chat { get; } = new();
    public PlayersOnline Players { get; } = new();
    public List<Notice> Notices { get; } = [];
    public int? PendingRollbackVersion { get; private set; }
    public ServerShutdown? PendingShutdown { get; private set; }

    public event Action<WorldObject>? ObjectAdded;
    public event Action<long>? ObjectRemoved;
    public event Action<ChatMessageEvent>? ChatReceived;

    public void LoadSnapshot(WorldObjects objects, ChatHistory history, IEnumerable<PlayerRef> online)
    {
        Reset();
        foreach (var o in objects.Objects) Objects.Add(o);
        foreach (var m in history.Messages) Chat.Add(m);
        foreach (var p in online) Players.Join(p);
    }

    public void Apply(ServerEvent ev)
    {
        switch (ev.BodyCase)
        {
            case ServerEvent.BodyOneofCase.WorldObjectAdded:
                if (Objects.Add(ev.WorldObjectAdded.Object)) ObjectAdded?.Invoke(ev.WorldObjectAdded.Object);
                break;
            case ServerEvent.BodyOneofCase.WorldObjectRemoved:
                if (Objects.Remove(ev.WorldObjectRemoved.ObjectId)) ObjectRemoved?.Invoke(ev.WorldObjectRemoved.ObjectId);
                break;
            case ServerEvent.BodyOneofCase.Chat:
                Chat.Add(ev.Chat);
                ChatReceived?.Invoke(ev.Chat);
                break;
            case ServerEvent.BodyOneofCase.PlayerJoined:
                Players.Join(ev.PlayerJoined.Player);
                break;
            case ServerEvent.BodyOneofCase.PlayerLeft:
                Players.Leave(ev.PlayerLeft.Player.PlayerId);
                break;
            case ServerEvent.BodyOneofCase.Notice:
                Notices.Add(ev.Notice);
                break;
            case ServerEvent.BodyOneofCase.SaveRolledBack:
                PendingRollbackVersion = ev.SaveRolledBack.Version;
                break;
            case ServerEvent.BodyOneofCase.Shutdown:
                PendingShutdown = ev.Shutdown;
                break;
            // SessionReplaced is handled by the connection; unknown bodies (newer server) are skipped.
        }
    }

    /// <summary>The game layer calls these once it has shown the pending notice.</summary>
    public void ClearPendingShutdown() => PendingShutdown = null;
    public void ClearPendingRollback() => PendingRollbackVersion = null;

    public void Reset()
    {
        Objects.Clear();
        Chat.Clear();
        Players.Clear();
        Notices.Clear();
        PendingRollbackVersion = null;
        PendingShutdown = null;
    }
}

public sealed class WorldObjectsIndex
{
    private readonly Dictionary<long, WorldObject> byId = [];
    private readonly Dictionary<int, WorldObject> settlementsByTile = [];

    public IReadOnlyCollection<WorldObject> All => byId.Values;

    public WorldObject? TryGet(long id) => byId.GetValueOrDefault(id);

    public WorldObject? TryGetByTile(int tile) => settlementsByTile.GetValueOrDefault(tile);

    /// <summary>False when an object with this id is already known (replayed event).</summary>
    public bool Add(WorldObject o)
    {
        if (!byId.TryAdd(o.Id, o)) return false;
        if (o.Type == "settlement") settlementsByTile[o.Tile] = o;
        return true;
    }

    public bool Remove(long id)
    {
        if (!byId.Remove(id, out var o)) return false;
        if (o.Type == "settlement" && settlementsByTile.TryGetValue(o.Tile, out var at) && at.Id == id) settlementsByTile.Remove(o.Tile);
        return true;
    }

    public void Clear()
    {
        byId.Clear();
        settlementsByTile.Clear();
    }
}

public sealed class ChatLog
{
    private readonly List<ChatMessageEvent> messages = [];
    private readonly HashSet<long> ids = [];

    public IReadOnlyList<ChatMessageEvent> Messages => messages;

    /// <summary>Ignores a message already stored: the history fetched at login overlaps the first live events.</summary>
    public void Add(ChatMessageEvent m)
    {
        if (m.Id != 0 && !ids.Add(m.Id)) return;
        messages.Add(m);
        if (messages.Count > Limits.ChatStored)
        {
            int drop = messages.Count - Limits.ChatStored;
            for (int i = 0; i < drop; i++) ids.Remove(messages[i].Id);
            messages.RemoveRange(0, drop);
        }
    }

    public void Clear()
    {
        messages.Clear();
        ids.Clear();
    }
}

public sealed class PlayersOnline
{
    private readonly Dictionary<long, PlayerRef> players = [];

    public IReadOnlyCollection<PlayerRef> Online => players.Values;

    public void Join(PlayerRef p) => players[p.PlayerId] = p;

    public void Leave(long playerId) => players.Remove(playerId);

    public void Clear() => players.Clear();
}
