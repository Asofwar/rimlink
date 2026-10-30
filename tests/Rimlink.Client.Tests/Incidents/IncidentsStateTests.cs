using Rimlink.Client.Incidents;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Incidents;

/// <summary>The client's view of paid incidents (paid incidents spec §4).</summary>
public class IncidentsStateTests
{
    private static Incident Hostile(long id, IncidentState state = IncidentState.IncidentQueued, long fireTick = 0) =>
        new() { Id = id, Kind = IncidentKind.IncidentHostile, State = state, FireTick = fireTick, EntryId = "pirates", Level = 2 };

    private static IncidentList ListOf(params Incident[] incidents)
    {
        var list = new IncidentList();
        list.Incidents.AddRange(incidents);
        return list;
    }

    [Fact]
    public void LevelFactors()
    {
        Assert.Equal([0.5, 1.0, 1.5, 2.0, 1.0], new[] { 1, 2, 3, 4, 0 }.Select(IncidentLevels.Factor).ToArray());
    }

    [Fact]
    public void LoadAndEventsKeepTheQueue()
    {
        var state = new IncidentsState();
        var catalog = new IncidentCatalog();
        catalog.Entries.Add(new CatalogEntry { Id = "pirates", Kind = IncidentKind.IncidentHostile });
        state.Load(ListOf(Hostile(1)), ListOf(Hostile(10)), catalog);
        var queued = new List<long>();
        state.Queued += i => queued.Add(i.Id);

        var ev = new ServerEvent { IncidentQueued = new IncidentQueued { Incident = Hostile(2) } };
        state.Apply(ev);
        state.Apply(ev); // replayed after a reconnect

        Assert.Equal([1L, 2L], state.Incoming.Select(i => i.Id).ToArray());
        Assert.Equal([2L], queued.ToArray());
        Assert.Equal([10L], state.Mine.Select(i => i.Id).ToArray());
        Assert.Equal("pirates", Assert.Single(state.Catalog.Entries).Id);

        state.Reset();
        Assert.Empty(state.Incoming);
        Assert.Empty(state.Mine);
        Assert.Empty(state.Catalog.Entries);
    }

    [Fact]
    public void DueSkipsFiredIds()
    {
        var state = new IncidentsState();
        state.Load(ListOf(Hostile(5, IncidentState.IncidentAnnounced, fireTick: 100), Hostile(6)), new IncidentList(), new IncidentCatalog());

        Assert.Empty(state.Due(100, new HashSet<long> { 5 }));
        Assert.Equal([5L], state.Due(100, new HashSet<long>()).Select(i => i.Id).ToArray());
        Assert.Empty(state.Due(99, new HashSet<long>()));

        // Update replaces the stored descriptor (after Announce returned the fire tick).
        state.Update(Hostile(6, IncidentState.IncidentAnnounced, fireTick: 50));
        Assert.Equal([6L], state.Due(60, new HashSet<long>()).Select(i => i.Id).ToArray());
    }

    [Fact]
    public void ResolvedRemovesFromIncoming()
    {
        var state = new IncidentsState();
        state.Load(ListOf(Hostile(1), Hostile(2)), ListOf(Hostile(10)), new IncidentCatalog());
        var resolved = new List<long>();
        state.Resolved += r => resolved.Add(r.Incident.Id);

        state.Apply(new ServerEvent { IncidentResolved = new IncidentResolved { Incident = Hostile(1, IncidentState.IncidentCancelled) } });
        state.Apply(new ServerEvent { IncidentResolved = new IncidentResolved { Incident = Hostile(10, IncidentState.IncidentFailed), Refund = 300 } });

        Assert.Equal([2L], state.Incoming.Select(i => i.Id).ToArray());
        Assert.Equal(IncidentState.IncidentFailed, Assert.Single(state.Mine).State);
        Assert.Equal([1L, 10L], resolved.ToArray());
    }

    [Fact]
    public void AReloadDoesNotBringBackWhatThisSessionHandled()
    {
        // A resync can read Pending just before the report lands: the stale row must not fire the paid incident a second time.
        var state = new IncidentsState();
        var stale = ListOf(Hostile(5, IncidentState.IncidentAnnounced, fireTick: 100));
        state.Load(stale, new IncidentList(), new IncidentCatalog());
        state.Remove(5);

        state.Load(stale, new IncidentList(), new IncidentCatalog());
        state.Apply(new ServerEvent { IncidentQueued = new IncidentQueued { Incident = Hostile(5) } });

        Assert.Empty(state.Incoming);
        Assert.Empty(state.Due(100, new HashSet<long>()));

        // A new session starts from what the server and the save say.
        state.Reset();
        state.Load(stale, new IncidentList(), new IncidentCatalog());
        Assert.Equal([5L], state.Incoming.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void AnyHostileWaitingIgnoresWeatherAndKind()
    {
        var state = new IncidentsState();
        state.Load(ListOf(
            new Incident { Id = 1, Kind = IncidentKind.IncidentWeather, State = IncidentState.IncidentQueued },
            new Incident { Id = 2, Kind = IncidentKind.IncidentKind, State = IncidentState.IncidentAnnounced }), new IncidentList(), new IncidentCatalog());

        Assert.False(state.AnyHostileWaiting);

        state.Apply(new ServerEvent { IncidentQueued = new IncidentQueued { Incident = Hostile(3) } });

        Assert.True(state.AnyHostileWaiting);
    }
}
