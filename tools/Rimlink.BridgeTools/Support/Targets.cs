using RimWorld;
using Rimlink.Client.Game;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>Resolves tool arguments to game and session objects. Main thread only.</summary>
public static class Targets
{
    /// <summary>Another player by id or name (case-insensitive): online players and owners of known settlements.</summary>
    public static PlayerRef? Player(string nameOrId)
    {
        long me = RimlinkSession.MyPlayerId;
        var known = new Dictionary<long, PlayerRef>();
        foreach (var o in RimlinkSession.State.Objects.All) if (o.Owner is not null) known[o.Owner.PlayerId] = o.Owner;
        foreach (var p in RimlinkSession.State.Players.Online) known[p.PlayerId] = p;
        known.Remove(me);
        string wanted = nameOrId.Trim();
        if (long.TryParse(wanted, out long id)) return known.GetValueOrDefault(id);
        return known.Values.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Our pawns on the current map from a comma-separated list of ids (GetUniqueLoadID, as rimworld/list_colonists
    /// returns them) or names. Null with <paramref name="error"/> naming every token that matched nothing or several pawns.
    /// </summary>
    public static List<Pawn>? Pawns(string csv, out string? error)
    {
        error = null;
        var ours = Find.CurrentMap.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer).ToList();
        var result = new List<Pawn>();
        var problems = new List<string>();
        foreach (string token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var matches = ours.Where(p => p.GetUniqueLoadID() == token).ToList();
            if (matches.Count == 0)
                matches = ours.Where(p => string.Equals(p.LabelShort, token, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(p.Name?.ToStringFull, token, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) { if (!result.Contains(matches[0])) result.Add(matches[0]); }
            else problems.Add(matches.Count == 0 ? $"no pawn '{token}'" : $"'{token}' matches {matches.Count} pawns");
        }
        if (problems.Count > 0) { error = string.Join("; ", problems); return null; }
        if (result.Count == 0) { error = "no pawns given"; return null; }
        return result;
    }

    /// <summary>
    /// Stacks to send from "def:count,def:count": from under the orbital trade beacons, as the economy window picks them,
    /// or from anywhere on the map. Null with <paramref name="error"/> on an unknown def or too few things.
    /// </summary>
    public static List<Rimlink.Client.Game.Economy.Outgoing.Pick>? Items(Map map, string csv, bool anywhere, out string? error)
    {
        error = null;
        var source = (anywhere
                ? map.listerThings.AllThings.Where(t => t.Spawned && t.def.EverHaulable)
                : TradeUtility.AllLaunchableThingsForTrade(map))
            .ToList();
        var picks = new List<Rimlink.Client.Game.Economy.Outgoing.Pick>();
        // One entry per def: two passes over the same stacks would pick one stack twice.
        var wanted = ItemSpec.Parse(csv, out var problems);
        foreach (var (name, count) in wanted)
        {
            var def = DefDatabase<ThingDef>.AllDefs.FirstOrDefault(d => string.Equals(d.defName, name, StringComparison.OrdinalIgnoreCase));
            if (def is null) { problems.Add($"unknown def '{name}'"); continue; }
            int left = count;
            foreach (var stack in source.Where(t => t.def == def))
            {
                if (left <= 0) break;
                int take = Math.Min(left, stack.stackCount);
                picks.Add(new(stack, take));
                left -= take;
            }
            if (left > 0) problems.Add($"only {count - left} of {count} {def.defName} {(anywhere ? "on the map" : "under the beacons")}");
        }
        if (problems.Count > 0) { error = string.Join("; ", problems); return null; }
        return picks;
    }

    /// <summary>Common checks of the in-game actions; null when the game is ready for one.</summary>
    public static object? NotPlaying()
    {
        if (!RimlinkSession.Active) return Bridge.Fail("no Rimlink session");
        if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap is null) return Bridge.Fail($"no map: the game is in {Current.ProgramState}");
        return null;
    }

    public static object Started(string message, object? details = null) => new { success = true, started = true, message, details };

    /// <summary>
    /// Runs a game entry point that refuses with a RejectInput message (busy, limits, a save that cannot be written now)
    /// and reports that refusal instead of "started".
    /// </summary>
    public static object Act(Action action, string message, object? details = null)
    {
        int frame = GameMessages.Frame;
        action();
        return GameMessages.RejectionSince(frame) is { } refusal ? Bridge.Fail(refusal, details) : Started(message, details);
    }
}
