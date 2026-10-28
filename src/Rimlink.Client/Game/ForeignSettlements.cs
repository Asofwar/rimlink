using RimWorld;
using RimWorld.Planet;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Mirrors other players' settlements from <see cref="SharedState"/> onto the world map. Main thread only.</summary>
public static class ForeignSettlements
{
    private static readonly Dictionary<long, PlayerSettlement> spawned = [];
    private static bool subscribed;

    public static WorldObjectDef Def => DefDatabase<WorldObjectDef>.GetNamed("Rimlink_PlayerSettlement");

    public static void Subscribe()
    {
        if (subscribed) return;
        subscribed = true;
        RimlinkSession.State.ObjectAdded += Add;
        RimlinkSession.State.ObjectRemoved += Remove;
    }

    /// <summary>Called once the world exists (after load or generation): spawn everything the snapshot holds.</summary>
    public static void RebuildFromState()
    {
        Clear();
        foreach (var o in RimlinkSession.State.Objects.All) Add(o);
    }

    public static void Clear()
    {
        if (Find.World != null)
        {
            foreach (var s in spawned.Values) if (s.Spawned) Find.WorldObjects.Remove(s);
        }
        spawned.Clear();
    }

    private static void Add(Protocol.V1.WorldObject o)
    {
        if (Find.World == null || o.Type != "settlement" || o.Owner.PlayerId == RimlinkSession.MyPlayerId) return;
        if (spawned.ContainsKey(o.Id)) return;

        var settlement = (PlayerSettlement)WorldObjectMaker.MakeWorldObject(Def);
        settlement.ObjectId = o.Id;
        settlement.OwnerName = o.Owner.Name;
        settlement.SettlementName = o.Name;
        settlement.ColorArgb = o.Owner.ColorArgb;
        settlement.Tile = o.Tile;
        Find.WorldObjects.Add(settlement);
        spawned[o.Id] = settlement;
    }

    private static void Remove(long id)
    {
        if (!spawned.Remove(id, out var settlement)) return;
        if (Find.World != null && settlement.Spawned) Find.WorldObjects.Remove(settlement);
    }

    /// <summary>The server-side id of the player's own settlement on this tile, if the state knows one.</summary>
    public static long? OwnObjectIdAt(int tile)
    {
        var o = RimlinkSession.State.Objects.TryGetByTile(tile);
        return o is not null && o.Owner.PlayerId == RimlinkSession.MyPlayerId ? o.Id : null;
    }
}
