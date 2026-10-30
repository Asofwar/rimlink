using Rimlink.Protocol.V1;

namespace Rimlink.Client.Incidents;

/// <summary>What the player sees of paid incidents: those ordered against them and those they bought. Fed by the event stream.</summary>
public sealed class IncidentsState
{
    private readonly List<Incident> incoming = [];
    // Handled in this session. A resync can read Pending before our report lands; the stale row must not run the incident again.
    private readonly HashSet<long> handled = [];

    /// <summary>Paid incidents waiting for this colony (queued or announced), oldest first.</summary>
    public IReadOnlyList<Incident> Incoming => incoming;
    /// <summary>Incidents this player ordered, as the server last listed them, kept current by the resolved events.</summary>
    public List<Incident> Mine { get; } = [];
    public IncidentCatalog Catalog { get; private set; } = new();

    public event Action<Incident>? Queued;
    public event Action<IncidentResolved>? Resolved;

    /// <summary>A hostile paid incident waits for this colony: outflow closes as under a raid (weather and kind events do not).</summary>
    public bool AnyHostileWaiting => incoming.Any(i => i.Kind == IncidentKind.IncidentHostile);

    public void Load(IncidentList incoming, IncidentList mine, IncidentCatalog catalog)
    {
        this.incoming.Clear();
        foreach (var i in incoming.Incidents) Add(i);
        Mine.Clear();
        Mine.AddRange(mine.Incidents);
        Catalog = catalog;
    }

    /// <summary>Replaces the stored descriptor (after Announce returned the fire tick).</summary>
    public void Update(Incident incident)
    {
        int i = incoming.FindIndex(x => x.Id == incident.Id);
        if (i >= 0) incoming[i] = incident;
    }

    /// <summary>Announced incoming incidents whose fire tick has come and that this game has not fired yet.</summary>
    public IEnumerable<Incident> Due(long ticksGame, ISet<long> fired) =>
        incoming.Where(i => i.State == IncidentState.IncidentAnnounced && i.FireTick <= ticksGame && !fired.Contains(i.Id)).ToList();

    /// <summary>Drops an incident from the queue for this session (fired or failed here, or no longer waiting on the server).</summary>
    public void Remove(long incidentId)
    {
        handled.Add(incidentId);
        incoming.RemoveAll(i => i.Id == incidentId);
    }

    public void Apply(ServerEvent ev)
    {
        switch (ev.BodyCase)
        {
            case ServerEvent.BodyOneofCase.IncidentQueued:
                if (Add(ev.IncidentQueued.Incident)) Queued?.Invoke(ev.IncidentQueued.Incident);
                break;
            case ServerEvent.BodyOneofCase.IncidentResolved:
                // The buyer hears every outcome; the target hears a cancelled or expired one.
                var incident = ev.IncidentResolved.Incident;
                Remove(incident.Id);
                int i = Mine.FindIndex(m => m.Id == incident.Id);
                if (i >= 0) Mine[i] = incident;
                Resolved?.Invoke(ev.IncidentResolved);
                break;
        }
    }

    public void Reset()
    {
        incoming.Clear();
        handled.Clear();
        Mine.Clear();
        Catalog = new IncidentCatalog();
    }

    /// <summary>False when the incident is already known or handled (replayed event, stale list).</summary>
    private bool Add(Incident incident)
    {
        if (handled.Contains(incident.Id) || incoming.Any(i => i.Id == incident.Id)) return false;
        incoming.Add(incident);
        return true;
    }
}
