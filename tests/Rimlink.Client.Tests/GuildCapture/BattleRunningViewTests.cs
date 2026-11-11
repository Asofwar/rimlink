using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleRunningViewTests
{
    [Fact]
    public void A_missing_delta_baseline_recovers_from_a_full_frame_and_old_data_cannot_rewind_the_view()
    {
        var start = BattleManifestContractsTests.Manifest();
        var publisher = new BattleRunningViewBuffer(start);
        var viewer = new BattleRunningViewBuffer(start);
        var first = Frame(start, 0);
        var second = Frame(start, 4);
        second.Pawns[0].Pawn.X++;
        Assert.True(publisher.Apply(first.ToByteString(), requireFull: true));
        Assert.True(publisher.Apply(second.ToByteString(), requireFull: true));

        var delta = BattleManifestContracts.ReadRunningView(publisher.ReadAfter(first.Sequence).Span, start);
        Assert.Equal(first.Sequence, delta.BaseSequence);
        Assert.Single(delta.Pawns);
        Assert.False(viewer.Apply(delta.ToByteString()));
        Assert.True(viewer.NeedsSnapshot);
        Assert.True(viewer.Apply(publisher.ReadAfter(0)));
        Assert.False(viewer.NeedsSnapshot);
        Assert.Equal(second, viewer.Current);
        Assert.False(viewer.Apply(first.ToByteString()));
        Assert.Equal(second, viewer.Current);

        var following = Frame(start, 8);
        following.Pawns[0].Pawn.X += 2;
        Assert.True(publisher.Apply(following.ToByteString(), requireFull: true));
        var update = publisher.ReadAfter(second.Sequence);
        Assert.True(viewer.Apply(update));
        Assert.False(viewer.Apply(update));
        Assert.Equal(following, viewer.Current);
        following.Pawns[0].Pawn.X++;
        Assert.Throws<InvalidDataException>(() => viewer.Apply(following.ToByteString()));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("digest")]
    [InlineData("side")]
    [InlineData("roster")]
    [InlineData("health")]
    [InlineData("position")]
    [InlineData("tick")]
    public void Foreign_or_impossible_frames_cannot_change_the_view(string fault)
    {
        var start = BattleManifestContractsTests.Manifest();
        var view = new BattleRunningViewBuffer(start);
        var baseline = Frame(start, 0);
        Assert.True(view.Apply(baseline.ToByteString()));
        var next = Frame(start, 4);
        switch (fault)
        {
            case "scope": next.Scope.Fence++; break;
            case "digest": next.StartDigest = ByteString.CopyFrom(new byte[32]); break;
            case "side": next.Pawns[0].Pawn.Side = BattleFighterSide.Defender; break;
            case "roster": next.Pawns.RemoveAt(7); break;
            case "health": next.Pawns[0].Pawn.Health = float.NaN; break;
            case "position": next.Pawns[0].Pawn.X = start.Arena.Width; break;
            case "tick": next.Tick = start.InitialTick - 1; break;
        }
        Assert.Throws<InvalidDataException>(() => view.Apply(next.ToByteString()));
        Assert.Equal(baseline, view.Current);
    }

    private static BattleRunningViewV1 Frame(BattlePreparedManifestV1 start, int elapsed)
    {
        var frame = new BattleRunningViewV1
        {
            Version = 1,
            Scope = new() { WorldInstanceId = start.WorldInstanceId, RestoreBoundary = start.RestoreBoundary, BattleId = start.BattleId,
                Attempt = start.Attempt, Fence = start.Fence },
            StartDigest = ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest, start.ToByteArray())),
            Sequence = elapsed + 1L,
            Tick = start.InitialTick + elapsed
        };
        foreach (var fighter in start.Entities)
            frame.Pawns.Add(new BattleRunningPawn
            {
                Pawn = new() { FighterId = fighter.FighterId, EntityId = fighter.EntityId, Side = fighter.Side, X = 25, Z = 25,
                    Alive = true, CanFight = true, Health = 1 },
                Spawned = true
            });
        return frame;
    }
}
