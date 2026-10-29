using Rimlink.Protocol.V1;

namespace Rimlink.Client.Live;

public sealed record ApplyResult(int UnknownIds);

/// <summary>
/// Turns two mirror states into the deltas between them (split so no frame exceeds the size limit) and applies
/// deltas back: spawns first, then changes, so a thing spawned and changed within one frame is known by the time
/// its change arrives. Ids are the host's; an id the mirror does not know is counted, never thrown.
/// </summary>
public static class DeltaCodec
{
    public static List<Delta> Diff(MirrorMap prev, MirrorMap next, int maxBytes)
    {
        var items = new List<Action<Delta>>();
        foreach (var (id, p) in next.Pawns)
        {
            if (!prev.Pawns.TryGetValue(id, out var old))
            {
                var spawned = new ThingSpawned { ThingId = id, Cell = new Cell { X = p.X, Z = p.Z } };
                if (p.Pawn is not null) spawned.Pawn = p.Pawn;
                var delta = p.ToDelta();
                items.Add(d => { d.ThingsSpawned.Add(spawned); d.Pawns.Add(delta); });
            }
            else if (!old.SameAs(p)) { var delta = p.ToDelta(); items.Add(d => d.Pawns.Add(delta)); }
        }
        foreach (var id in prev.Pawns.Keys) if (!next.Pawns.ContainsKey(id)) items.Add(d => d.ThingsDespawned.Add(id));
        foreach (var (id, t) in next.Things)
        {
            if (!prev.Things.TryGetValue(id, out var old))
            {
                var spawned = new ThingSpawned { ThingId = id, Cell = new Cell { X = t.X, Z = t.Z } };
                if (t.Item is not null) spawned.Item = t.Item;
                var changed = new ThingChanged { ThingId = id, Cell = new Cell { X = t.X, Z = t.Z }, StackCount = t.StackCount, Hp = t.Hp };
                items.Add(d => { d.ThingsSpawned.Add(spawned); d.ThingsChanged.Add(changed); });
            }
            else if (!old.SameAs(t))
            {
                var changed = new ThingChanged { ThingId = id, Cell = new Cell { X = t.X, Z = t.Z }, StackCount = t.StackCount, Hp = t.Hp };
                items.Add(d => d.ThingsChanged.Add(changed));
            }
        }
        foreach (var id in prev.Things.Keys) if (!next.Things.ContainsKey(id)) items.Add(d => d.ThingsDespawned.Add(id));
        foreach (var (cell, v) in next.Cells)
        {
            if (prev.Cells.TryGetValue(cell, out var old) && old == v) continue;
            var change = new CellChange { Cell = new Cell { X = cell.X, Z = cell.Z }, Kind = v.Kind, Value = v.Value };
            items.Add(d => d.Cells.Add(change));
        }
        foreach (var (cell, v) in prev.Cells)
        {
            if (next.Cells.ContainsKey(cell)) continue;
            var change = new CellChange { Cell = new Cell { X = cell.X, Z = cell.Z }, Kind = v.Kind, Value = "" };
            items.Add(d => d.Cells.Add(change));
        }
        foreach (var (cell, fireSize) in next.Fires)
        {
            if (prev.Fires.TryGetValue(cell, out var old) && old == fireSize) continue;
            var change = new FireChange { Cell = new Cell { X = cell.X, Z = cell.Z }, Size = fireSize };
            items.Add(d => d.Fires.Add(change));
        }
        foreach (var cell in prev.Fires.Keys)
        {
            if (next.Fires.ContainsKey(cell)) continue;
            var change = new FireChange { Cell = new Cell { X = cell.X, Z = cell.Z }, Gone = true };
            items.Add(d => d.Fires.Add(change));
        }

        var frames = new List<Delta>();
        var current = Header(next);
        int size = current.CalculateSize();
        foreach (var add in items)
        {
            var probe = new Delta();
            add(probe);
            int itemSize = probe.CalculateSize();
            if (size + itemSize > maxBytes && (current.Pawns.Count + current.ThingsSpawned.Count + current.ThingsChanged.Count + current.ThingsDespawned.Count + current.Cells.Count + current.Fires.Count) > 0)
            {
                frames.Add(current);
                current = Header(next);
                size = current.CalculateSize();
            }
            add(current);
            size += itemSize;
        }
        frames.Add(current);
        return frames;
    }

    private static Delta Header(MirrorMap m) => new() { Tick = m.Tick, Speed = m.Speed, PausedBy = m.PausedBy, SnapshotSeq = m.SnapshotSeq, HostWish = m.HostWish, GuestWish = m.GuestWish };

    public static ApplyResult Apply(MirrorMap map, Delta d)
    {
        map.Tick = d.Tick;
        map.Speed = d.Speed;
        map.PausedBy = d.PausedBy;
        map.SnapshotSeq = d.SnapshotSeq;
        map.HostWish = d.HostWish;
        map.GuestWish = d.GuestWish;
        int unknown = 0;
        foreach (var s in d.ThingsSpawned)
        {
            if (s.Pawn is not null) map.Pawns[s.ThingId] = new MirrorPawn { Id = s.ThingId, X = s.Cell?.X ?? 0, Z = s.Cell?.Z ?? 0, Pawn = s.Pawn, HealthPct = 100 };
            else map.Things[s.ThingId] = new MirrorThing { Id = s.ThingId, X = s.Cell?.X ?? 0, Z = s.Cell?.Z ?? 0, StackCount = s.Item?.Count ?? 1, Hp = s.Item?.HitPoints ?? 0, Item = s.Item };
        }
        foreach (long id in d.ThingsDespawned)
        {
            map.Things.Remove(id);
            map.Pawns.Remove(id);
        }
        foreach (var c in d.ThingsChanged)
        {
            if (!map.Things.TryGetValue(c.ThingId, out var t)) { unknown++; continue; }
            if (c.Cell is not null) { t.X = c.Cell.X; t.Z = c.Cell.Z; }
            t.StackCount = c.StackCount;
            t.Hp = c.Hp;
        }
        foreach (var p in d.Pawns)
        {
            if (!map.Pawns.TryGetValue(p.ThingId, out var pawn)) { unknown++; continue; }
            pawn.Apply(p);
        }
        foreach (var c in d.Cells)
        {
            var cell = (c.Cell?.X ?? 0, c.Cell?.Z ?? 0);
            if (c.Value.Length == 0) map.Cells.Remove(cell); else map.Cells[cell] = (c.Kind, c.Value);
        }
        foreach (var f in d.Fires)
        {
            var cell = (f.Cell?.X ?? 0, f.Cell?.Z ?? 0);
            if (f.Gone) map.Fires.Remove(cell); else map.Fires[cell] = f.Size;
        }
        return new ApplyResult(unknown);
    }
}
