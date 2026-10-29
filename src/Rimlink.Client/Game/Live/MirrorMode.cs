using HarmonyLib;
using RimWorld;
using Rimlink.Protocol.V1;
using Verse;
using Verse.AI.Group;

namespace Rimlink.Client.Game.Live;

/// <summary>
/// The guest's view of the host's live map (spec §4): the loaded copy never simulates anything; its clock is set from
/// the host's deltas, its pawns and things are moved by <see cref="MirrorApplier"/>, and the player's orders for their
/// own pawns become frames instead of actions (Patch_Live_Mirror). Main thread only.
/// </summary>
public static class MirrorMode
{
    public static bool Active { get; private set; }
    public static LiveSession? Session { get; private set; }
    public static Map? Map { get; private set; }
    /// <summary>The host's ids of this player's pawns.</summary>
    public static HashSet<int> OwnedIds { get; } = [];
    /// <summary>True while the applier writes the mirror; the input patches let these writes through.</summary>
    public static bool Applying { get; set; }
    public static long TargetTick { get; set; }
    public static int PausedBy { get; set; }
    public static int SnapshotVersion { get; set; }
    /// <summary>The game pauses itself right after a load; speed writes in the first seconds are not the player's wish.</summary>
    public static DateTime EnteredAt { get; private set; }
    public static bool SettlingIn => DateTime.UtcNow - EnteredAt < TimeSpan.FromSeconds(3);

    public static bool IsOwned(Thing t) => Active && OwnedIds.Contains(t.thingIDNumber);

    private const int OwnIdsFrom = 1_000_000_000;

    private static readonly AccessTools.FieldRef<UniqueIDsManager, int> NextThingId = AccessTools.FieldRefAccess<UniqueIDsManager, int>("nextThingID");

    /// <summary>
    /// Things the copy makes itself (chunks while its maps load, the visit host faction's pawns) take ids from the counter the host's
    /// save carried, the very ids the host gives its next things: a host thing arriving under such an id was taken for one
    /// already there and never spawned ("held by ChunkGranite101076"). As soon as the counter is read, the copy's own things
    /// move to ids far above the host's.
    /// </summary>
    public static void TakeOwnIdRange(UniqueIDsManager ids)
    {
        if (NextThingId(ids) < OwnIdsFrom) NextThingId(ids) += OwnIdsFrom;
    }

    /// <summary>After the host's copy loaded and the visit loader re-factioned it: find this player's pawns and freeze the clock.</summary>
    public static void Enter(LiveSession session, int snapshotVersion)
    {
        Session = session;
        SnapshotVersion = snapshotVersion;
        Map = Find.CurrentMap ?? Find.Maps.FirstOrDefault() ?? throw new InvalidOperationException("the host's save has no map");
        OwnedIds.Clear();
        var owners = MapComponent_LiveOwners.Of(Map);
        foreach (int? id in owners.IdsOf(RimlinkSession.MyPlayerId)) if (id is { } i) OwnedIds.Add(i);
        Applying = true;
        try
        {
            TargetTick = Find.TickManager.TicksGame;
            Active = true;
            EnteredAt = DateTime.UtcNow;
            ownershipPending = true; // the faction switch waits for the first frame of play: during FinalizeInit the pawn caches do not follow it
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }
        finally { Applying = false; }
        RimlinkLog.Instance.Info($"mirror: entered live session {session.Id} on {Map} with {OwnedIds.Count} own pawns (snapshot v{snapshotVersion})");
    }

    private static bool ownershipPending;

    /// <summary>First frame of play on the copy: our pawns join the player faction so they can be selected and drafted.</summary>
    public static void ClaimOwnPawns()
    {
        if (!ownershipPending || !Active || Map is null || Current.ProgramState != ProgramState.Playing) return;
        ownershipPending = false;
        Applying = true;
        try
        {
            foreach (var pawn in Map.mapPawns.AllPawnsSpawned.ToList())
                if (OwnedIds.Contains(pawn.thingIDNumber)) Claim(pawn);
            Find.ColonistBar?.MarkColonistsDirty();
        }
        catch (Exception ex) { Log.Error("[Rimlink] mirror: own pawns could not be claimed: " + ex); }
        finally { Applying = false; }
    }

    /// <summary>
    /// One of our pawns joins the player faction here: at the first frame, and again whenever the host's blob brings it back
    /// (carried off and put down), since that blob carries the host's faction (the raiders').
    /// </summary>
    public static void Claim(Pawn pawn)
    {
        if (pawn.GetLord() is { } lord) lord.Notify_PawnLost(pawn, PawnLostCondition.Undefined);
        if (pawn.Faction != Faction.OfPlayer) pawn.SetFaction(Faction.OfPlayer);
        PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn);
    }

    public static void Exit()
    {
        ownershipPending = false;
        Active = false;
        Session = null;
        Map = null;
        OwnedIds.Clear();
        PausedBy = 0;
    }

    /// <summary>In place of the vanilla tick: the clock jumps to the last delta's tick; nothing else runs.</summary>
    public static void Advance()
    {
        var tm = Find.TickManager;
        if (tm is null) return;
        if (TargetTick > tm.TicksGame) Traverse.Create(tm).Field("ticksGameInt").SetValue((int)TargetTick);
    }

    public static void SetLocalSpeed(TimeSpeed speed)
    {
        Applying = true;
        try { Find.TickManager.CurTimeSpeed = speed; }
        finally { Applying = false; }
    }
}
