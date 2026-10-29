using Google.Protobuf;
using Rimlink.Client.Live;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Live;

/// <summary>The Verse-free mirror model: a delta between two states, applied to the first, gives the second.</summary>
public class DeltaCodecTests
{
    private static MirrorMap RandomMap(Random rng, int pawns, int things)
    {
        var map = new MirrorMap { Tick = 1000, Speed = 1, SnapshotSeq = 1 };
        for (int i = 1; i <= pawns; i++)
            map.Pawns[i] = new MirrorPawn { Id = i, X = rng.Next(250), Z = rng.Next(250), Rot = rng.Next(4), JobDef = "Wait", HealthPct = 100 };
        for (int i = 1; i <= things; i++)
            map.Things[1000 + i] = new MirrorThing { Id = 1000 + i, X = rng.Next(250), Z = rng.Next(250), StackCount = 1 + rng.Next(75), Hp = 100, Item = Blob(1000 + i, 32) };
        for (int i = 0; i < 50; i++) map.Cells[(rng.Next(250), rng.Next(250))] = ("roof", "RoofConstructed");
        return map;
    }

    private static ItemBlob Blob(long id, int bytes) => new() { DefName = "Steel", Count = 1, Quality = -1, Label = "thing " + id, ScribeXmlZstd = ByteString.CopyFrom(new byte[bytes]) };

    private static void Mutate(MirrorMap next, Random rng)
    {
        var pawnIds = next.Pawns.Keys.ToList();
        for (int i = 0; i < 20; i++)
        {
            var p = next.Pawns[pawnIds[rng.Next(pawnIds.Count)]];
            p.X = rng.Next(250); p.Z = rng.Next(250); p.Drafted = !p.Drafted; p.JobDef = "Goto"; p.JobTarget = new Target { Cell = new Cell { X = 1, Z = 2 } }; p.HealthPct = 50;
        }
        next.Pawns[pawnIds[0]].Dead = true;
        var thingIds = next.Things.Keys.ToList();
        for (int i = 0; i < 10; i++) next.Things[thingIds[i]].StackCount += 1;
        for (int i = 10; i < 15; i++) next.Things.Remove(thingIds[i]);
        for (int i = 1; i <= 10; i++) next.Things[5000 + i] = new MirrorThing { Id = 5000 + i, X = i, Z = i, StackCount = 3, Hp = 90, Item = Blob(5000 + i, 64) };
        next.Pawns[9000] = new MirrorPawn { Id = 9000, X = 5, Z = 5, HealthPct = 100, Pawn = new PawnBlob { KindDef = "Colonist", Name = "Newcomer", ScribeXmlZstd = ByteString.CopyFrom(new byte[64]) } };
        next.Cells[(3, 3)] = ("terrain", "Soil");
        next.Cells.Remove(next.Cells.Keys.First());
        next.Fires[(7, 7)] = 0.5f;
        next.Tick = 1006;
        next.Speed = 2;
        next.PausedBy = 0;
    }

    [Fact]
    public void DiffThenApplyReproducesTheMap()
    {
        var rng = new Random(7);
        var prev = RandomMap(rng, 500, 500);
        var next = prev.Clone();
        Mutate(next, rng);

        var deltas = DeltaCodec.Diff(prev, next, 256 * 1024);
        var replay = prev.Clone();
        int unknown = 0;
        foreach (var d in deltas) unknown += DeltaCodec.Apply(replay, d).UnknownIds;

        Assert.Single(deltas);
        Assert.Equal(0, unknown);
        Assert.True(next.SameAs(replay), "the applied map differs from the target");
        Assert.Equal(1006, replay.Tick);
        Assert.True(deltas[0].Pawns.Count is >= 21 and <= 22); // only the changed pawns travel
        Assert.Equal(5, deltas[0].ThingsDespawned.Count);
        Assert.Equal(11, deltas[0].ThingsSpawned.Count); // 10 things and the newcomer
    }

    [Fact]
    public void LargeDiffIsSplitUnderMaxBytes()
    {
        var prev = new MirrorMap();
        var next = prev.Clone();
        for (int i = 1; i <= 3000; i++) next.Things[i] = new MirrorThing { Id = i, X = i % 250, Z = i / 250, StackCount = 1, Hp = 100, Item = Blob(i, 200) };

        var deltas = DeltaCodec.Diff(prev, next, 64 * 1024);

        Assert.True(deltas.Count > 1);
        Assert.All(deltas, d => Assert.True(d.CalculateSize() <= 64 * 1024, $"frame of {d.CalculateSize()} bytes"));
        var replay = prev.Clone();
        foreach (var d in deltas) DeltaCodec.Apply(replay, d);
        Assert.True(next.SameAs(replay));
    }

    [Fact]
    public void UnknownIdsAreCountedNotThrown()
    {
        var map = new MirrorMap();
        map.Pawns[1] = new MirrorPawn { Id = 1 };
        var delta = new Delta { Tick = 6 };
        delta.Pawns.Add(new PawnDelta { ThingId = 1, Cell = new Cell { X = 2, Z = 2 } });
        delta.Pawns.Add(new PawnDelta { ThingId = 42, Cell = new Cell { X = 2, Z = 2 } });
        delta.ThingsChanged.Add(new ThingChanged { ThingId = 43, StackCount = 5 });
        delta.ThingsDespawned.Add(44); // gone before we knew it: not an unknown, nothing to do

        var result = DeltaCodec.Apply(map, delta);

        Assert.Equal(2, result.UnknownIds);
        Assert.Equal(2, map.Pawns[1].X);
        Assert.Equal(6, map.Tick);
    }

    [Fact]
    public void SpawnedBeforeChangedWithinAFrame()
    {
        var map = new MirrorMap();
        var delta = new Delta { Tick = 6 };
        delta.ThingsChanged.Add(new ThingChanged { ThingId = 7, StackCount = 9, Hp = 80, Cell = new Cell { X = 4, Z = 4 } });
        delta.ThingsSpawned.Add(new ThingSpawned { ThingId = 7, Cell = new Cell { X = 1, Z = 1 }, Item = Blob(7, 16) });
        delta.ThingsSpawned.Add(new ThingSpawned { ThingId = 8, Cell = new Cell { X = 1, Z = 1 }, Pawn = new PawnBlob { KindDef = "Colonist", Name = "P" } });
        delta.Pawns.Add(new PawnDelta { ThingId = 8, Cell = new Cell { X = 3, Z = 3 }, Drafted = true });

        var result = DeltaCodec.Apply(map, delta);

        Assert.Equal(0, result.UnknownIds);
        Assert.Equal(9, map.Things[7].StackCount);
        Assert.Equal(4, map.Things[7].X);
        Assert.True(map.Pawns[8].Drafted);
        Assert.Equal(3, map.Pawns[8].X);
    }
}
