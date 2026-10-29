using Verse;

namespace Rimlink.Client.Game.Live;

/// <summary>
/// Which guest owns which pawn on this map, and at which index of the guest's escrow list, saved with the map so a
/// reload (or the host's next login after a crash) still knows whose pawns these are (spec §5).
/// </summary>
public sealed class MapComponent_LiveOwners(Map map) : MapComponent(map)
{
    private Dictionary<int, long> owners = [];
    private Dictionary<int, int> indexes = [];
    private List<int>? keys1, keys2;
    private List<long>? values1;
    private List<int>? values2;

    public static MapComponent_LiveOwners Of(Map map) => map.GetComponent<MapComponent_LiveOwners>();

    public void Mark(Thing t, long owner, int index)
    {
        owners[t.thingIDNumber] = owner;
        indexes[t.thingIDNumber] = index;
    }

    public long? OwnerOf(Thing t) => owners.TryGetValue(t.thingIDNumber, out long o) ? o : null;

    public bool IsOwned(Thing t) => owners.ContainsKey(t.thingIDNumber);

    public bool Any => owners.Count > 0;

    /// <summary>The guest's pawn ids in escrow order (index → id); a missing index is null.</summary>
    public List<int?> IdsOf(long owner)
    {
        var list = new List<int?>();
        foreach (var (id, o) in owners)
        {
            if (o != owner) continue;
            int index = indexes.TryGetValue(id, out int i) ? i : list.Count;
            while (list.Count <= index) list.Add(null);
            list[index] = id;
        }
        return list;
    }

    public IEnumerable<long> Owners => owners.Values.Distinct();

    public void ForgetAll(long owner)
    {
        foreach (int id in owners.Where(kv => kv.Value == owner).Select(kv => kv.Key).ToList())
        {
            owners.Remove(id);
            indexes.Remove(id);
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref owners, "rimlinkLiveOwners", LookMode.Value, LookMode.Value, ref keys1, ref values1);
        Scribe_Collections.Look(ref indexes, "rimlinkLiveIndexes", LookMode.Value, LookMode.Value, ref keys2, ref values2);
        owners ??= [];
        indexes ??= [];
    }
}
