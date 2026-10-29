using HarmonyLib;
using RimWorld;
using Rimlink.Client.Game.Economy;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Live;

/// <summary>Writes one delta into the mirrored map: spawns first, then changes, then pawns, cells, fires and damage effects. Returns the count of unknown ids.</summary>
public static class MirrorApplier
{
    public static int Apply(Delta d)
    {
        if (!MirrorMode.Active || MirrorMode.Map is not { } map) return 0;
        int unknown = 0;
        MirrorMode.Applying = true;
        try
        {
            MirrorMode.TargetTick = d.Tick;
            MirrorMode.PausedBy = d.PausedBy;
            var speed = d.PausedBy != 0 ? TimeSpeed.Paused : (TimeSpeed)Math.Clamp(d.Speed, 0, 3);
            if (Find.TickManager.CurTimeSpeed != speed) Find.TickManager.CurTimeSpeed = speed;

            foreach (var s in d.ThingsSpawned) Spawn(s, map);
            foreach (long id in d.ThingsDespawned)
            {
                if (FindThing(id, map) is { } gone)
                {
                    if (gone is Pawn pawn) { PawnExit.Remove(pawn); continue; } // carried, dead (its corpse comes as a spawn) or gone: a later blob brings it back
                    if (gone.Spawned) gone.DeSpawn();
                    if (!gone.Destroyed) gone.Destroy(DestroyMode.Vanish);
                }
            }
            foreach (var c in d.ThingsChanged)
            {
                if (FindThing(c.ThingId, map) is not { } t || t is Pawn) { unknown++; Unknown("thing", c.ThingId, c.Cell); continue; }
                var cell = new IntVec3(c.Cell?.X ?? t.Position.x, 0, c.Cell?.Z ?? t.Position.z);
                if (t.Spawned && cell.InBounds(map) && t.Position != cell) t.Position = cell;
                if (t.def.stackLimit > 1 && c.StackCount > 0) t.stackCount = c.StackCount;
                if (t.def.useHitPoints && c.Hp > 0) t.HitPoints = c.Hp;
            }
            foreach (var p in d.Pawns)
            {
                if (FindThing(p.ThingId, map) is not Pawn pawn) { unknown++; Unknown("pawn", p.ThingId, p.Cell); continue; }
                var cell = new IntVec3(p.Cell?.X ?? pawn.Position.x, 0, p.Cell?.Z ?? pawn.Position.z);
                if (pawn.Spawned && cell.InBounds(map) && pawn.Position != cell) pawn.Position = cell;
                pawn.Rotation = new Rot4(p.Rot);
                if (pawn.drafter is not null && pawn.drafter.Drafted != p.Drafted) pawn.drafter.Drafted = p.Drafted;
            }
            foreach (var c in d.Cells)
            {
                var cell = new IntVec3(c.Cell?.X ?? 0, 0, c.Cell?.Z ?? 0);
                if (!cell.InBounds(map)) continue;
                if (c.Kind == "roof") map.roofGrid.SetRoof(cell, c.Value.Length == 0 ? null : DefDatabase<RoofDef>.GetNamedSilentFail(c.Value));
                else if (c.Kind == "terrain" && c.Value.Length > 0 && DefDatabase<TerrainDef>.GetNamedSilentFail(c.Value) is { } terrain) map.terrainGrid.SetTerrain(cell, terrain);
            }
            foreach (var f in d.Fires)
            {
                var cell = new IntVec3(f.Cell?.X ?? 0, 0, f.Cell?.Z ?? 0);
                if (!cell.InBounds(map)) continue;
                var fire = cell.GetThingList(map).OfType<Fire>().FirstOrDefault();
                if (f.Gone) { fire?.Destroy(DestroyMode.Vanish); continue; }
                fire ??= (Fire)GenSpawn.Spawn(ThingDefOf.Fire, cell, map);
                fire.fireSize = f.Size;
            }
            foreach (var e in d.Damage)
            {
                if (FindThing(e.VictimId, map) is { Spawned: true } victim) MoteMaker.ThrowText(victim.DrawPos, map, e.Amount.ToString("0"), 1.5f);
            }
            foreach (var blob in d.GuestPawnBlobs) Transplant(blob, map);
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] mirror: a delta could not be applied: " + ex);
        }
        finally { MirrorMode.Applying = false; }
        return unknown;
    }

    private static void Spawn(ThingSpawned s, Map map)
    {
        if (FindThing(s.ThingId, map) is { Spawned: true }) return; // already there (the snapshot had it)
        var cell = new IntVec3(s.Cell?.X ?? 0, 0, s.Cell?.Z ?? 0);
        if (!cell.InBounds(map)) return;
        try
        {
            Thing thing;
            if (s.Pawn is not null) thing = ThingBlobs.DeserializePawnRaw(s.Pawn);
            else if (s.Item is not null) thing = ThingBlobs.DeserializeItem(s.Item);
            else return;
            ThingBlobs.ResetMapIndex(thing);
            GenSpawn.Spawn(thing, cell, map, WipeMode.VanishOrMoveAside);
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn($"mirror: thing {s.ThingId} could not be spawned: {ex.Message}");
        }
    }

    /// <summary>
    /// The host's full state of one of this player's pawns: health is taken over whole (the rest is cosmetic until the next snapshot).
    /// The deserialised copy is dropped, never destroyed: a Thing equals any Thing with the same id, and a destroyed pawn is passed to
    /// the world pawns, where it would make the real pawn "a world pawn" (unselectable) and be saved under its id.
    /// </summary>
    private static void Transplant(PawnBlob blob, Map map)
    {
        try
        {
            var fresh = ThingBlobs.DeserializePawnRaw(blob);
            ThingBlobs.ResetMapIndex(fresh);
            if (FindThing(fresh.thingIDNumber, map) is not Pawn pawn || !MirrorMode.OwnedIds.Contains(pawn.thingIDNumber)) return;
            var health = fresh.health;
            Traverse.Create(health).Field("pawn").SetValue(pawn);
            health.hediffSet.pawn = pawn;
            foreach (var h in health.hediffSet.hediffs) h.pawn = pawn;
            pawn.health = health;
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn("mirror: a pawn blob could not be applied: " + ex.Message);
        }
    }

    private static int unknownLogged;

    private static void Unknown(string kind, long id, Cell? cell)
    {
        if (unknownLogged++ < 12) RimlinkLog.Instance.Warn($"mirror: unknown {kind} {id} at ({cell?.X},{cell?.Z})");
    }

    private static Thing? FindThing(long id, Map map) =>
        map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id) ?? map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id);
}
