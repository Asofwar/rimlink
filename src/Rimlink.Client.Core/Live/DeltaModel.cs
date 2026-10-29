using Rimlink.Protocol.V1;

namespace Rimlink.Client.Live;

/// <summary>A pawn as the mirror knows it (spec §3 PawnDelta fields); <see cref="Pawn"/> is the blob of a pawn the snapshot did not have.</summary>
public sealed class MirrorPawn
{
    public long Id;
    public int X, Z, Rot;
    public bool Drafted, Downed, Dead;
    public string JobDef = "";
    public Target? JobTarget;
    public int HealthPct;
    public string MentalStateDef = "";
    public long CarriedThingId;
    public int PathX = -1, PathZ = -1;
    public PawnBlob? Pawn;

    public MirrorPawn Clone() => (MirrorPawn)MemberwiseClone();

    public bool SameAs(MirrorPawn o) =>
        X == o.X && Z == o.Z && Rot == o.Rot && Drafted == o.Drafted && Downed == o.Downed && Dead == o.Dead && JobDef == o.JobDef
        && Equals(JobTarget, o.JobTarget) && HealthPct == o.HealthPct && MentalStateDef == o.MentalStateDef && CarriedThingId == o.CarriedThingId
        && PathX == o.PathX && PathZ == o.PathZ;

    public PawnDelta ToDelta()
    {
        var d = new PawnDelta { ThingId = Id, Cell = new Cell { X = X, Z = Z }, Rot = Rot, Drafted = Drafted, Downed = Downed, Dead = Dead, JobDef = JobDef,
            HealthPct = HealthPct, MentalStateDef = MentalStateDef, CarriedThingId = CarriedThingId };
        if (JobTarget is not null) d.JobTarget = JobTarget;
        if (PathX >= 0) d.PathNextCell = new Cell { X = PathX, Z = PathZ };
        return d;
    }

    public void Apply(PawnDelta d)
    {
        if (d.Cell is not null) { X = d.Cell.X; Z = d.Cell.Z; }
        Rot = d.Rot; Drafted = d.Drafted; Downed = d.Downed; Dead = d.Dead; JobDef = d.JobDef; JobTarget = d.JobTarget;
        HealthPct = d.HealthPct; MentalStateDef = d.MentalStateDef; CarriedThingId = d.CarriedThingId;
        if (d.PathNextCell is not null) { PathX = d.PathNextCell.X; PathZ = d.PathNextCell.Z; } else { PathX = -1; PathZ = -1; }
    }
}

/// <summary>A thing as the mirror knows it; <see cref="Item"/> is the blob of a thing the snapshot did not have.</summary>
public sealed class MirrorThing
{
    public long Id;
    public int X, Z;
    public int StackCount;
    public int Hp;
    public ItemBlob? Item;

    public MirrorThing Clone() => (MirrorThing)MemberwiseClone();

    public bool SameAs(MirrorThing o) => X == o.X && Z == o.Z && StackCount == o.StackCount && Hp == o.Hp;
}

/// <summary>Everything the host mirrors to the guest, keyed by the host's ids. Verse-free, so the codec can be tested without the game.</summary>
public sealed class MirrorMap
{
    public long Tick;
    public int Speed;
    public int PausedBy;
    public int SnapshotSeq;
    /// <summary>The speed each side asked for (0 paused, 1..3): shown on both players' time buttons.</summary>
    public int HostWish;
    public int GuestWish;
    public Dictionary<long, MirrorPawn> Pawns { get; } = [];
    public Dictionary<long, MirrorThing> Things { get; } = [];
    public Dictionary<(int X, int Z), (string Kind, string Value)> Cells { get; } = [];
    public Dictionary<(int X, int Z), float> Fires { get; } = [];

    public MirrorMap Clone()
    {
        var m = new MirrorMap { Tick = Tick, Speed = Speed, PausedBy = PausedBy, SnapshotSeq = SnapshotSeq, HostWish = HostWish, GuestWish = GuestWish };
        foreach (var (id, p) in Pawns) m.Pawns[id] = p.Clone();
        foreach (var (id, t) in Things) m.Things[id] = t.Clone();
        foreach (var (c, v) in Cells) m.Cells[c] = v;
        foreach (var (c, v) in Fires) m.Fires[c] = v;
        return m;
    }

    public bool SameAs(MirrorMap o)
    {
        if (Tick != o.Tick || Speed != o.Speed || PausedBy != o.PausedBy || SnapshotSeq != o.SnapshotSeq || HostWish != o.HostWish || GuestWish != o.GuestWish) return false;
        if (Pawns.Count != o.Pawns.Count || Things.Count != o.Things.Count || Cells.Count != o.Cells.Count || Fires.Count != o.Fires.Count) return false;
        foreach (var (id, p) in Pawns) if (!o.Pawns.TryGetValue(id, out var q) || !p.SameAs(q)) return false;
        foreach (var (id, t) in Things) if (!o.Things.TryGetValue(id, out var u) || !t.SameAs(u)) return false;
        foreach (var (c, v) in Cells) if (!o.Cells.TryGetValue(c, out var w) || v != w) return false;
        foreach (var (c, v) in Fires) if (!o.Fires.TryGetValue(c, out var w) || v != w) return false;
        return true;
    }
}
