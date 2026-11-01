// Minimal collection Scribe double: tests exercise the production map component's save fields, not the game engine.
namespace Verse;

public class Map
{
    public T GetComponent<T>() => throw new NotSupportedException();
}

public abstract class MapComponent
{
    protected MapComponent(Map map) { }
    public virtual void ExposeData() { }
}

public class Thing { public int thingIDNumber; }
public sealed class Pawn : Thing
{
    public PawnEquipment? equipment;
    public PawnApparel? apparel;
    public PawnInventory? inventory;
}
public sealed class PawnEquipment { public List<Thing> AllEquipmentListForReading = []; }
public sealed class PawnApparel { public List<Thing> WornApparel = []; }
public sealed class PawnInventory { public List<Thing> innerContainer = []; }
public enum LookMode { Value }

public static class Scribe_Collections
{
    public static bool Loading;
    public static Dictionary<string, object> Fields = [];

    public static void Look<TKey, TValue>(ref Dictionary<TKey, TValue> items, string label, LookMode keyMode,
        LookMode valueMode, ref List<TKey>? keys, ref List<TValue>? values) where TKey : notnull
    {
        if (Loading)
            items = Fields.TryGetValue(label, out var stored) ? new((Dictionary<TKey, TValue>)stored) : [];
        else Fields[label] = new Dictionary<TKey, TValue>(items);
    }

    public static void Look<T>(ref HashSet<T> items, string label, LookMode mode)
    {
        if (Loading) items = Fields.TryGetValue(label, out var stored) ? new((HashSet<T>)stored) : [];
        else Fields[label] = new HashSet<T>(items);
    }
}
