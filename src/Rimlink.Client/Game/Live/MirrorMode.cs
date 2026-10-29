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

    public static bool IsOwned(Thing t) => Active && OwnedIds.Contains(t.thingIDNumber);

    /// <summary>After the host's copy loaded and the visit loader re-factioned it: find this player's pawns and freeze the clock.</summary>
    public static void Enter(LiveSession session, int snapshotVersion)
    {
        Session = session;
        SnapshotVersion = snapshotVersion;
        Map = Find.CurrentMap ?? Find.Maps.FirstOrDefault() ?? throw new InvalidOperationException("the host's save has no map");
        OwnedIds.Clear();
        var owners = MapComponent_LiveOwners.Of(Map);
        foreach (int? id in owners.IdsOf(RimlinkSession.MyPlayerId)) if (id is { } i) OwnedIds.Add(i);
        var player = Faction.OfPlayer;
        Applying = true;
        try
        {
            foreach (var pawn in Map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (!OwnedIds.Contains(pawn.thingIDNumber)) continue;
                if (pawn.Faction != player) pawn.SetFaction(player);
                if (pawn.GetLord() is { } lord) lord.Notify_PawnLost(pawn, PawnLostCondition.Undefined);
                PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn);
            }
            TargetTick = Find.TickManager.TicksGame;
            Active = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }
        finally { Applying = false; }
        RimlinkLog.Instance.Info($"mirror: entered live session {session.Id} on {Map} with {OwnedIds.Count} own pawns (snapshot v{snapshotVersion})");
    }

    public static void Exit()
    {
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
