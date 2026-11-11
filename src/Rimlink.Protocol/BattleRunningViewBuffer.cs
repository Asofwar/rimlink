using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static partial class BattleManifestContracts
{
    public const int RunningViewBytes = 4096; // Only eight fixed fighters; no world data or arbitrary blobs.

    public static BattleRunningViewV1 ReadRunningView(ReadOnlySpan<byte> bytes, BattlePreparedManifestV1 start)
    {
        var frame = Read(bytes, RunningViewBytes, BattleRunningViewV1.Parser);
        long elapsed = (long)frame.Tick - start.InitialTick;
        if (frame.Version != 1 || frame.Scope is not { } scope || scope.WorldInstanceId != start.WorldInstanceId
            || scope.RestoreBoundary != start.RestoreBoundary || scope.BattleId != start.BattleId || scope.Attempt != start.Attempt
            || scope.Fence != start.Fence || !frame.StartDigest.Span.SequenceEqual(Digest(BattleDocumentKind.PreparedManifest, start.ToByteArray()))
            || elapsed is < 0 or > 30000 || frame.Sequence != elapsed + 1 || frame.BaseSequence < 0 || frame.BaseSequence >= frame.Sequence
            || frame.HeldTicks < 0 || frame.HeldTicks > Math.Min(elapsed, 3000) || start.Entities.Count != 8
            || frame.Pawns.Count > 8 || frame.BaseSequence == 0 && frame.Pawns.Count != 8) throw Invalid();

        int index = 0;
        foreach (var fighter in frame.Pawns)
        {
            if (fighter.Pawn is not { } pawn || fighter.Rotation is < 0 or > 3 || !float.IsFinite(pawn.Health) || pawn.Health is < 0 or > 1
                || pawn.X < 0 || pawn.Z < 0 || pawn.X >= start.Arena.Width || pawn.Z >= start.Arena.Height
                || pawn.CanFight && !fighter.Spawned) throw Invalid();
            while (index < start.Entities.Count && start.Entities[index].FighterId != pawn.FighterId) index++;
            if (index == start.Entities.Count || start.Entities[index].EntityId != pawn.EntityId || start.Entities[index].Side != pawn.Side)
                throw Invalid();
            index++;
        }
        return frame;
    }
}

/// <summary>The latest full state and its predecessor; a gap can always be repaired with one full frame.</summary>
public sealed class BattleRunningViewBuffer(BattlePreparedManifestV1 manifest)
{
    private readonly BattlePreparedManifestV1 start = manifest.Clone();
    private BattleRunningViewV1? previous, current, received;
    public BattleRunningViewV1? Current => current?.Clone();
    public bool NeedsSnapshot { get; private set; }

    public bool Apply(ByteString bytes, bool requireFull = false)
    {
        var frame = BattleManifestContracts.ReadRunningView(bytes.Span, start);
        if (requireFull && frame.BaseSequence != 0) throw Invalid();
        if (current is not null && frame.Sequence <= current.Sequence)
        {
            if (frame.Sequence < current.Sequence) return false;
            if (!frame.Equals(received) && !frame.Equals(current)) throw Invalid();
            if (frame.BaseSequence == 0) NeedsSnapshot = false;
            return false;
        }
        if (frame.BaseSequence != 0 && current?.Sequence != frame.BaseSequence)
        {
            NeedsSnapshot = true;
            return false;
        }
        received = frame;
        if (frame.BaseSequence != 0)
        {
            frame = frame.Clone();
            var changed = frame.Pawns.ToDictionary(fighter => fighter.Pawn.FighterId);
            frame.Pawns.Clear();
            frame.Pawns.Add(current!.Pawns.Select(fighter => changed.GetValueOrDefault(fighter.Pawn.FighterId, fighter)));
            frame.BaseSequence = 0;
        }
        previous = current;
        current = frame;
        NeedsSnapshot = false;
        return true;
    }

    public ByteString ReadAfter(long sequence)
    {
        if (sequence < 0) throw Invalid();
        if (current is null || current.Sequence == sequence) return ByteString.Empty;
        if (previous?.Sequence != sequence) return current.ToByteString();
        var delta = current.Clone();
        delta.BaseSequence = sequence;
        delta.Pawns.Clear();
        for (int i = 0; i < current.Pawns.Count; i++)
            if (!current.Pawns[i].Equals(previous.Pawns[i])) delta.Pawns.Add(current.Pawns[i]);
        return delta.ToByteString();
    }

    private static InvalidDataException Invalid() => new("battle-running-view-invalid");
}
