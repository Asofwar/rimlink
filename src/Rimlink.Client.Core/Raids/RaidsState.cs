using Rimlink.Protocol.V1;

namespace Rimlink.Client.Raids;

/// <summary>What the player sees of raids: raids coming at them, their own raids, their flags. Fed by the event stream.</summary>
public sealed class RaidsState
{
    private readonly List<Raid> incoming = [];
    private readonly HashSet<long> incomingIds = [];

    /// <summary>Raids aimed at me, queued or announced, oldest first.</summary>
    public IReadOnlyList<Raid> Incoming => incoming;
    public List<Raid> Mine { get; } = [];
    public PlayerFlags Flags { get; private set; } = new();

    public event Action<Raid>? RaidQueued;
    public event Action<RaidLanded>? RaidLanded;
    public event Action<RaidResolved>? RaidResolved;
    public event Action<RaidExpired>? RaidExpired;
    public event Action<ItemSold>? ItemSold;
    public event Action<VisitStarted>? VisitStarted;
    public event Action<PlayerFlags>? FlagsChanged;

    public void Load(RaidList pending, RaidList mine, PlayerFlags flags)
    {
        incoming.Clear();
        incomingIds.Clear();
        foreach (var r in pending.Raids) AddIncoming(r);
        Mine.Clear();
        Mine.AddRange(mine.Raids);
        Flags = flags;
    }

    /// <summary>False when the raid is already known (replayed event).</summary>
    public bool AddIncoming(Raid raid)
    {
        if (!incomingIds.Add(raid.Id)) return false;
        incoming.Add(raid);
        return true;
    }

    /// <summary>Replaces the stored descriptor (after Announce returned the landing tick).</summary>
    public void Update(Raid raid)
    {
        int i = incoming.FindIndex(r => r.Id == raid.Id);
        if (i >= 0) incoming[i] = raid;
    }

    public void MarkLanded(long raidId)
    {
        if (incomingIds.Remove(raidId)) incoming.RemoveAll(r => r.Id == raidId);
    }

    public void Apply(ServerEvent ev)
    {
        switch (ev.BodyCase)
        {
            case ServerEvent.BodyOneofCase.RaidQueued:
                if (AddIncoming(ev.RaidQueued.Raid)) RaidQueued?.Invoke(ev.RaidQueued.Raid);
                break;
            case ServerEvent.BodyOneofCase.RaidLanded:
                SetMineState(ev.RaidLanded.RaidId, RaidState.RaidLanded);
                RaidLanded?.Invoke(ev.RaidLanded);
                break;
            case ServerEvent.BodyOneofCase.RaidResolved:
                SetMineState(ev.RaidResolved.RaidId, RaidState.RaidResolved, ev.RaidResolved.Summary);
                RaidResolved?.Invoke(ev.RaidResolved);
                break;
            case ServerEvent.BodyOneofCase.RaidExpired:
                SetMineState(ev.RaidExpired.RaidId, RaidState.RaidExpired);
                RaidExpired?.Invoke(ev.RaidExpired);
                break;
            case ServerEvent.BodyOneofCase.ItemSold:
                ItemSold?.Invoke(ev.ItemSold);
                break;
            case ServerEvent.BodyOneofCase.VisitStarted:
                VisitStarted?.Invoke(ev.VisitStarted);
                break;
            case ServerEvent.BodyOneofCase.PlayerFlagsChanged:
                Flags = ev.PlayerFlagsChanged.Flags;
                FlagsChanged?.Invoke(Flags);
                break;
        }
    }

    private void SetMineState(long raidId, RaidState state, RaidSummary? summary = null)
    {
        var raid = Mine.FirstOrDefault(r => r.Id == raidId);
        if (raid is null) return;
        raid.State = state;
        if (summary is not null) raid.Summary = summary;
    }

    public void Reset()
    {
        incoming.Clear();
        incomingIds.Clear();
        Mine.Clear();
        Flags = new PlayerFlags();
    }
}
