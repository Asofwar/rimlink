using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Incidents;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Incidents;

/// <summary>
/// Paid incidents ordered against this player run as vanilla incidents on the home map (paid incidents spec §3): announced
/// (a warning letter when the server set the fire tick ahead), fired with forced parms once the fire tick came, then reported.
/// A fired id stays in the game component until the server took the report, so a reload before the answer neither fires it
/// again nor loses the report. Main thread only.
/// </summary>
public static class IncidentIncoming
{
    private static readonly HashSet<long> announcing = [];
    private static readonly HashSet<long> reporting = [];
    // Failures whose report did not reach the server; nothing ran in the game, so they are not saved with it.
    private static readonly Dictionary<long, string> failedUnsent = [];
    private static DateTime lastAttempt = DateTime.MinValue;

    public static void Reset()
    {
        announcing.Clear();
        reporting.Clear();
        failedUnsent.Clear();
    }

    /// <summary>Called every frame by the game component; throttled, only on the home map, not in a visit or as a live guest.</summary>
    public static void Tick()
    {
        if (DateTime.UtcNow - lastAttempt < TimeSpan.FromSeconds(5)) return;
        lastAttempt = DateTime.UtcNow;
        if (!RimlinkSession.Active || Visits.VisitSession.InVisit || Live.LiveGuest.Session is not null || RimlinkSession.Incidents is not { } client
            || Current.ProgramState != ProgramState.Playing) return;
        var map = Find.CurrentMap;
        if (map is null || !map.IsPlayerHome) return;
        var component = Current.Game.GetComponent<RimlinkGameComponent>();
        var state = RimlinkSession.State.Incidents;

        foreach (long id in component.FiredIncidents.ToList())
            if (!reporting.Contains(id)) Report(client, id, fired: true, "");
        foreach (var (id, reason) in failedUnsent.ToList())
            if (!reporting.Contains(id)) Report(client, id, fired: false, reason);

        foreach (var incident in state.Incoming.Where(i => i.State == IncidentState.IncidentQueued).ToList())
            if (announcing.Add(incident.Id)) Announce(client, incident);

        var fired = component.FiredIncidents.ToHashSet();
        foreach (var incident in state.Due(Find.TickManager.TicksGame, fired)) Fire(client, incident, map, component);
    }

    private static void Announce(IncidentClient client, Incident incident)
    {
        long id = incident.Id;
        long tick = Find.TickManager.TicksGame;
        _ = Task.Run(async () =>
        {
            try
            {
                var updated = await client.Announce(id, tick, CancellationToken.None);
                MainThread.Post(() =>
                {
                    RimlinkSession.State.Incidents.Update(updated);
                    RimlinkLog.Instance.Info($"incident {id} announced at tick {tick}, fires at {updated.FireTick}");
                    if (updated.FireTick <= tick || Current.ProgramState != ProgramState.Playing) return;
                    string label = LabelOf(updated);
                    Find.LetterStack.ReceiveLetter("Rimlink.Incident.WarningLabel".Translate(label),
                        "Rimlink.Incident.Warning".Translate(updated.Buyer?.Name ?? "?", label, updated.Level), LetterDefOf.ThreatBig);
                });
            }
            catch (RpcException ex) when (EconomyClient.CodeOf(ex) == ErrorCode.RaidGone || ex.StatusCode == StatusCode.NotFound)
            {
                RimlinkLog.Instance.Warn($"incident {id} is gone: {EconomyClient.MessageOf(ex)}");
                MainThread.Post(() => RimlinkSession.State.Incidents.Remove(id));
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn($"incident {id} could not be announced, retrying: {ex.Message}");
                MainThread.Post(() => announcing.Remove(id));
            }
        });
    }

    private static void Fire(IncidentClient client, Incident incident, Map map, RimlinkGameComponent component)
    {
        long id = incident.Id;
        RimlinkSession.State.Incidents.Remove(id);
        var def = DefDatabase<IncidentDef>.GetNamedSilentFail(incident.IncidentDef);
        if (def is null)
        {
            Report(client, id, fired: false, $"incident def {incident.IncidentDef} is missing in the target's game");
            return;
        }
        // Recorded before the run: a save made while it runs must not let a reload fire it a second time.
        component.FiredIncidents.Add(id);
        string reason = "";
        bool ok;
        try
        {
            ok = def.Worker.TryExecute(Parms(def, incident, map));
            if (!ok) reason = $"{def.defName} could not start on this map";
        }
        catch (Exception ex)
        {
            ok = false;
            reason = $"{def.defName} failed: {ex.Message}";
            RimlinkLog.Instance.Warn($"incident {id} threw: {ex}");
        }
        if (!ok)
        {
            component.FiredIncidents.Remove(id);
            Report(client, id, fired: false, reason);
            return;
        }
        string buyer = incident.Buyer?.Name ?? "?";
        Messages.Message((incident.Kind == IncidentKind.IncidentKind ? "Rimlink.Incident.GiftFrom" : "Rimlink.Incident.OrderedBy").Translate(buyer),
            MessageTypeDefOf.NeutralEvent, historical: false);
        RimlinkLog.Instance.Info($"incident {id} ({def.defName}, level {incident.Level}) fired, ordered by {buyer}");
        Report(client, id, fired: true, "");
    }

    /// <summary>Forced parms on the map (or the world for world-only incidents); hostile ones at a share of this colony's threat points.</summary>
    private static IncidentParms Parms(IncidentDef def, Incident incident, Map map)
    {
        IIncidentTarget target = !def.TargetAllowed(map) && def.TargetAllowed(Find.World) ? Find.World : map;
        var parms = StorytellerUtility.DefaultParmsNow(def.category, target);
        parms.forced = true;
        if (incident.Kind != IncidentKind.IncidentHostile) return parms;
        parms.points = StorytellerUtility.DefaultThreatPointsNow(map) * (float)IncidentLevels.Factor(incident.Level);
        if (def.Worker is IncidentWorker_Raid)
        {
            // A faction of that def friendly to us would walk in as raiders it is not: the worker then picks a hostile one.
            if (DefDatabase<FactionDef>.GetNamedSilentFail(incident.FactionDef) is { } factionDef
                && Find.FactionManager.FirstFactionOfDef(factionDef) is { } faction && faction.HostileTo(Faction.OfPlayer))
                parms.faction = faction;
            parms.raidArrivalMode = incident.Pods ? PawnsArrivalModeDefOf.EdgeDrop : PawnsArrivalModeDefOf.EdgeWalkIn;
        }
        return parms;
    }

    private static void Report(IncidentClient client, long id, bool fired, string reason)
    {
        reporting.Add(id);
        failedUnsent.Remove(id);
        _ = Task.Run(async () =>
        {
            try
            {
                await client.Report(id, fired, reason, CancellationToken.None);
                MainThread.Post(() => Reported(id, fired, reason));
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // Not ours any more (a reset colony): nothing to retry.
                MainThread.Post(() => Reported(id, fired, reason));
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn($"incident {id} report not sent, retrying: {ex.Message}");
                MainThread.Post(() =>
                {
                    reporting.Remove(id);
                    if (!fired) failedUnsent[id] = reason;
                });
            }
        });
    }

    private static void Reported(long id, bool fired, string reason)
    {
        reporting.Remove(id);
        Current.Game?.GetComponent<RimlinkGameComponent>()?.FiredIncidents.Remove(id);
        RimlinkSession.State.Incidents.Remove(id);
        RimlinkLog.Instance.Info($"incident {id} reported {(fired ? "fired" : "failed: " + reason)}");
    }

    private static string LabelOf(Incident incident) =>
        DefDatabase<IncidentDef>.GetNamedSilentFail(incident.IncidentDef)?.LabelCap.ToString() ?? incident.EntryId;
}
