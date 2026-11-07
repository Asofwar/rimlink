namespace Verse
{
    public interface IExposable { void ExposeData(); }
    public interface IThingHolder { }
    public abstract class GameComponent : IExposable
    {
        public virtual void ExposeData() { }
    }
    public sealed class Game
    {
        public T GetComponent<T>() => throw new NotSupportedException();
    }
    public static class Current { public static Game? Game; }
    public partial class Thing
    {
        public string GetUniqueLoadID() => "Thing_Human" + thingIDNumber;
    }
    public sealed partial class Pawn
    {
        public bool Dead;
        public RimWorld.Faction? Faction = RimWorld.Faction.OfPlayer;
    }
    public sealed class Corpse : Thing { public Pawn? InnerPawn; }
    public partial class Map : IThingHolder { }
    public sealed class WorldObjects { public List<object> AllWorldObjects = []; }
    public static class Find
    {
        public static List<Map> Maps = [];
        public static WorldObjects WorldObjects = new();
    }
    public enum LoadSaveMode { Inactive, Saving, LoadingVars }
    public enum ThingRequestGroup { Everything }
    public readonly struct ThingRequest
    {
        public static ThingRequest ForGroup(ThingRequestGroup group) => new();
    }
    public static class ThingOwnerUtility
    {
        public static void GetAllThingsRecursively(IThingHolder holder, List<Thing> contents) { }
        public static void GetAllThingsRecursively(Map map, ThingRequest request, List<Thing> contents) { }
    }
    public static class Scribe { public static LoadSaveMode mode; }
    public static partial class Scribe_Collections
    {
        public static int Writes;
        public static void Look<T>(ref List<T> items, string label, LookMode mode) => Writes++;
    }
    public static class Scribe_References
    {
        public static void Look<T>(ref T? value, string label) where T : class { }
    }
    public static class Scribe_Values
    {
        public static void Look<T>(ref T value, string label, T defaultValue = default!) { }
    }
}
namespace RimWorld
{
    public sealed class Faction { public static readonly Faction OfPlayer = new(); }
    public static class PawnsFinder { public static List<Verse.Pawn> AllMapsWorldAndTemporary_AliveOrDead = []; }
}
namespace Rimlink.Client.Game.Visits
{
    public static class VisitSession { public static bool InVisit; }
}
namespace Rimlink.Client.Game.Live
{
    public static class LiveGuest
    {
        public static Rimlink.Protocol.V1.LiveSession? Session;
        public static bool Loading;
    }
    public static class MirrorMode { public static bool Active; }
}
