using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.State;

/// <summary>Local copy of everything shared. Mutated only from the thread that drains the <see cref="EventInbox"/> (the game's main thread).</summary>
public sealed class SharedState
{
    public WorldObjectsIndex Objects { get; } = new();
    public ChatLog Chat { get; } = new();
    public PlayersOnline Players { get; } = new();
    public Economy.EconomyState Economy { get; } = new();
    public Raids.RaidsState Raids { get; } = new();
    public Live.LiveState Live { get; } = new();
    public Incidents.IncidentsState Incidents { get; } = new();
    public Social.SocialState Social { get; } = new();
    public List<Notice> Notices { get; } = [];
    public int? PendingRollbackVersion { get; private set; }
    public ServerShutdown? PendingShutdown { get; private set; }

    public event Action<WorldObject>? ObjectAdded;
    public event Action<long>? ObjectRemoved;
    /// <summary>A known object changed (an owner's wealth tier or colony age).</summary>
    public event Action<WorldObject>? ObjectUpdated;
    public event Action<ChatMessageEvent>? ChatReceived;
    /// <summary>A "look here" from another player (live UX spec §2); the game layer shows it only while a game is running.</summary>
    public event Action<PingEvent>? Pinged;
    public PingEvent? LastPing { get; private set; }

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
            case ServerEvent.BodyOneofCase.WorldObjectUpdated:
                if (Objects.Update(ev.WorldObjectUpdated.Object)) ObjectUpdated?.Invoke(ev.WorldObjectUpdated.Object);
                break;
            case ServerEvent.BodyOneofCase.Chat:
                Chat.Add(ev.Chat);
                ChatReceived?.Invoke(ev.Chat);
                break;
            case ServerEvent.BodyOneofCase.Ping:
                LastPing = ev.Ping;
                Pinged?.Invoke(ev.Ping);
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
            case ServerEvent.BodyOneofCase.ParcelArrived:
            case ServerEvent.BodyOneofCase.LotSold:
            case ServerEvent.BodyOneofCase.LotClosed:
            case ServerEvent.BodyOneofCase.WalletChanged:
                Economy.Apply(ev);
                break;
            case ServerEvent.BodyOneofCase.GiftRefused:
                Economy.Apply(ev);
                Social.Apply(ev);
                break;
            case ServerEvent.BodyOneofCase.TradeUpdated:
                Social.Apply(ev);
                Economy.MarkWealthStale(); // goods moved in or out of escrow without touching the wallet
                break;
            case ServerEvent.BodyOneofCase.RaidQueued:
            case ServerEvent.BodyOneofCase.RaidLanded:
            case ServerEvent.BodyOneofCase.RaidResolved:
            case ServerEvent.BodyOneofCase.RaidExpired:
            case ServerEvent.BodyOneofCase.ItemSold:
            case ServerEvent.BodyOneofCase.VisitStarted:
            case ServerEvent.BodyOneofCase.PlayerFlagsChanged:
                Raids.Apply(ev);
                break;
            case ServerEvent.BodyOneofCase.LiveInvite:
            case ServerEvent.BodyOneofCase.LiveStarted:
            case ServerEvent.BodyOneofCase.LiveEnded:
            case ServerEvent.BodyOneofCase.LiveFallback:
            case ServerEvent.BodyOneofCase.LivePeerLost:
            case ServerEvent.BodyOneofCase.LivePeerBack:
                Live.Apply(ev);
                break;
            case ServerEvent.BodyOneofCase.IncidentQueued:
            case ServerEvent.BodyOneofCase.IncidentResolved:
                Incidents.Apply(ev);
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
        Economy.Reset();
        Social.Reset();
        Raids.Reset();
        Live.Reset();
        Incidents.Reset();
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

    /// <summary>Replaces a known object by id; false (and nothing changes) for an unknown one.</summary>
    public bool Update(WorldObject o)
    {
        if (!byId.TryGetValue(o.Id, out var old)) return false;
        byId[o.Id] = o;
        if (old.Type == "settlement" && settlementsByTile.TryGetValue(old.Tile, out var at) && at.Id == o.Id) settlementsByTile.Remove(old.Tile);
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
