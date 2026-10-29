using RimWorld;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Live;
using Rimlink.Protocol.V1;
using Verse;
using Verse.AI;

namespace Rimlink.Client.Game.Live;

/// <summary>
/// Reads the host's map into the Verse-free mirror model. Pawns and fires are read every delta; things come from the
/// spawn, despawn and damage hooks between full rescans (every 10 s). Main thread only.
/// </summary>
public sealed class DeltaCollector(Map map)
{
    private readonly Dictionary<long, Thing> spawned = [];
    private readonly HashSet<long> despawned = [];
    private readonly HashSet<long> changed = [];
    private readonly List<DamageEvent> damage = [];
    private readonly List<CellChange> cells = [];

    public Map Map { get; } = map;
    /// <summary>The state the guest has (what the last delta brought it to); replaced after every delta.</summary>
    public MirrorMap Current { get; set; } = new();

    /// <summary>Pawns, items and buildings; plants stay as the snapshot had them (thousands of grass tiles are not worth the frames).</summary>
    public static bool Mirrored(Thing t) => t is Pawn || t.def.category is ThingCategory.Item or ThingCategory.Building;

    /// <summary>Everything the guest's snapshot holds, read at the snapshot tick.</summary>
    public MirrorMap FullScan(bool blobsForNew)
    {
        var m = new MirrorMap { Tick = Find.TickManager.TicksGame, Speed = (int)LiveTempo.Effective, PausedBy = LiveTempo.PausedBy, SnapshotSeq = Current.SnapshotSeq };
        foreach (var p in Map.mapPawns.AllPawnsSpawned) m.Pawns[p.thingIDNumber] = ReadPawn(p, blobsForNew && !Current.Pawns.ContainsKey(p.thingIDNumber));
        foreach (var t in Map.listerThings.AllThings)
        {
            if (t is Pawn || !Mirrored(t) || !t.Spawned) continue;
            m.Things[t.thingIDNumber] = ReadThing(t, blobsForNew && !Current.Things.ContainsKey(t.thingIDNumber));
        }
        ReadFires(m);
        foreach (var (c, v) in Current.Cells) m.Cells[c] = v;
        return m;
    }

    /// <summary>The next state: the previous one plus what the hooks saw, with every pawn re-read.</summary>
    public MirrorMap Next()
    {
        var m = Current.Clone();
        m.Tick = Find.TickManager.TicksGame;
        m.Speed = (int)LiveTempo.Effective;
        m.PausedBy = LiveTempo.PausedBy;
        foreach (long id in despawned) { m.Things.Remove(id); m.Pawns.Remove(id); }
        foreach (var (id, t) in spawned)
        {
            if (!t.Spawned || t.Map != Map || t.Destroyed) continue;
            if (t is Pawn) continue; // pawns are re-read below
            m.Things[id] = ReadThing(t, blob: !Current.Things.ContainsKey(id));
        }
        foreach (long id in changed)
        {
            if (!m.Things.TryGetValue(id, out var mt)) continue;
            if (FindThing(id) is { } t) { mt.X = t.Position.x; mt.Z = t.Position.z; mt.StackCount = t.stackCount; mt.Hp = t.def.useHitPoints ? t.HitPoints : 0; }
        }
        m.Pawns.Clear();
        foreach (var p in Map.mapPawns.AllPawnsSpawned) m.Pawns[p.thingIDNumber] = ReadPawn(p, blob: !Current.Pawns.ContainsKey(p.thingIDNumber));
        ReadFires(m);
        return m;
    }

    /// <summary>Damage and cell events since the last delta; cleared with <see cref="Commit"/>.</summary>
    public List<DamageEvent> Damage => damage;
    public List<CellChange> Cells => cells;
    /// <summary>Threat letters of the host's game since the last delta, for the guest's letter stack; cleared with <see cref="Commit"/>.</summary>
    public List<GuestMessage> Letters { get; } = [];

    /// <summary>A host letter as the guest's mirror shows it: label, text, def, and what it points at (a thing by id, else a cell).</summary>
    public void Letter(Letter letter)
    {
        var message = new GuestMessage { Letter = true, Label = letter.Label.ToString(), LetterDef = letter.def.defName, Text = (letter as ChoiceLetter)?.Text.ToString() ?? "" };
        if (letter.lookTargets?.PrimaryTarget is { IsValid: true } target)
            message.Look = target.HasThing ? new Target { ThingId = target.Thing.thingIDNumber } : new Target { Cell = new Cell { X = target.Cell.x, Z = target.Cell.z } };
        if (Letters.Count < 20) Letters.Add(message);
    }

    public void Commit(MirrorMap next)
    {
        foreach (var p in next.Pawns.Values) p.Pawn = null; // blobs travel once
        foreach (var t in next.Things.Values) t.Item = null;
        Current = next;
        spawned.Clear();
        despawned.Clear();
        changed.Clear();
        damage.Clear();
        cells.Clear();
        Letters.Clear();
    }

    public void Spawned(Thing t)
    {
        if (!Mirrored(t)) return;
        spawned[t.thingIDNumber] = t;
        despawned.Remove(t.thingIDNumber);
    }

    public void Despawned(Thing t)
    {
        if (!Mirrored(t)) return;
        despawned.Add(t.thingIDNumber);
        spawned.Remove(t.thingIDNumber);
    }

    public void Damaged(Thing victim, DamageInfo info, float amount)
    {
        if (!Mirrored(victim)) return;
        changed.Add(victim.thingIDNumber);
        if (damage.Count < 200)
            damage.Add(new DamageEvent { AttackerId = info.Instigator?.thingIDNumber ?? 0, VictimId = victim.thingIDNumber, Amount = amount, BodyPartDef = info.HitPart?.def.defName ?? "", DamageDef = info.Def?.defName ?? "" });
    }

    public void CellChanged(IntVec3 c, string kind, string value)
    {
        cells.Add(new CellChange { Cell = new Cell { X = c.x, Z = c.z }, Kind = kind, Value = value });
        if (value.Length == 0) Current.Cells.Remove((c.x, c.z)); else Current.Cells[(c.x, c.z)] = (kind, value);
    }

    private Thing? FindThing(long id) => Map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id);

    private void ReadFires(MirrorMap m)
    {
        m.Fires.Clear();
        foreach (var f in Map.listerThings.ThingsOfDef(ThingDefOf.Fire)) if (f is Fire fire) m.Fires[(fire.Position.x, fire.Position.z)] = fire.fireSize;
    }

    private static MirrorThing ReadThing(Thing t, bool blob)
    {
        var mt = new MirrorThing { Id = t.thingIDNumber, X = t.Position.x, Z = t.Position.z, StackCount = t.stackCount, Hp = t.def.useHitPoints ? t.HitPoints : 0 };
        if (blob)
        {
            try { mt.Item = ThingBlobs.SerializeItem(t); }
            catch (Exception ex) { RimlinkLog.Instance.Warn($"live: thing {t.ThingID} could not be serialised for the mirror: {ex.Message}"); }
        }
        return mt;
    }

    public static MirrorPawn ReadPawn(Pawn p, bool blob)
    {
        var mp = new MirrorPawn
        {
            Id = p.thingIDNumber,
            X = p.Position.x,
            Z = p.Position.z,
            Rot = p.Rotation.AsInt,
            Drafted = p.Drafted || OrderExecutor.VirtualDrafted.Contains(p.thingIDNumber),
            Downed = p.Downed,
            Dead = p.Dead,
            JobDef = p.CurJobDef?.defName ?? "",
            HealthPct = (int)(p.health.summaryHealth.SummaryHealthPercent * 100f),
            MentalStateDef = p.MentalStateDef?.defName ?? "",
            CarriedThingId = p.carryTracker?.CarriedThing?.thingIDNumber ?? 0,
        };
        if (p.CurJob is { } job && job.targetA.IsValid)
            mp.JobTarget = job.targetA.HasThing ? new Target { ThingId = job.targetA.Thing.thingIDNumber } : new Target { Cell = new Cell { X = job.targetA.Cell.x, Z = job.targetA.Cell.z } };
        if (p.pather is { MovingNow: true } pather) { mp.PathX = pather.nextCell.x; mp.PathZ = pather.nextCell.z; }
        if (blob)
        {
            try { mp.Pawn = ThingBlobs.SerializePawn(p); }
            catch (Exception ex) { RimlinkLog.Instance.Warn($"live: pawn {p.LabelShort} could not be serialised for the mirror: {ex.Message}"); }
        }
        return mp;
    }
}
