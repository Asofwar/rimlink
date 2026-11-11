using Google.Protobuf;
using System.Security.Cryptography;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static partial class BattleManifestContracts
{
    public static BattleTerminalViewV1? ReadCompletedView(BattleCompletedViewReply reply,BattlePreparedManifestV1 start)
    {
        if (reply.Abort is { } abort)
        {
            BattleCleanupContracts.ValidateTerminal(abort);
            if (reply.Receipt is not null || !reply.CommittedFinal.IsEmpty || !reply.View.IsEmpty
                || abort.Kind != BattleCleanupTerminalKind.StartedAbort || abort.AssignmentTransitionId != start.PreparationTransitionId
                || !abort.AbortedStartDigest.Span.SequenceEqual(Digest(BattleDocumentKind.PreparedManifest, start.ToByteArray()))) throw Invalid();
            return null;
        }
        if (reply.Receipt is not { } receipt)
        {
            if (!reply.CommittedFinal.IsEmpty || !reply.View.IsEmpty) throw Invalid();
            return null;
        }
        var committed=ReadCommittedFinal(reply.CommittedFinal.Span,start);
        var envelope=ReadFinalEnvelope(committed.ExecutorEnvelope.Span);
        var final=ReadFinal(envelope.Manifest.Span,start);
        bool captured=final.Outcome==BattleTerminalOutcome.AttackerWin;
        if (receipt.Version!=1 || receipt.WorldInstanceId!=start.WorldInstanceId || receipt.RestoreBoundary!=start.RestoreBoundary
            || receipt.BattleId!=start.BattleId || receipt.CampaignId!=start.CampaignId || receipt.OutpostId!=start.OutpostId
            || receipt.Attempt!=start.Attempt || receipt.Fence!=start.Fence || receipt.ClientRequestId!=envelope.ClientRequestId
            || !Uuid(receipt.TransitionId) || !receipt.CommittedFinalDigest.Span.SequenceEqual(Digest(BattleDocumentKind.CommittedFinal,reply.CommittedFinal.Span))
            || receipt.Outcome!=final.Outcome || receipt.Reason!=final.Reason || receipt.PreviousAllianceId!=start.DefenderAllianceId
            || receipt.CurrentAllianceId!=(captured?start.AttackerAllianceId:start.DefenderAllianceId)
            || receipt.PreviousOwnerRevision!=start.OwnerRevision || receipt.CurrentOwnerRevision!=start.OwnerRevision+(captured?1:0)
            || receipt.OldStockObligationCount!=(captured?start.StockAtCut:0)) throw Invalid();
        return ReadTerminalView(reply.View.Span,final);
    }

    public static BattleFinalEnvelope ReadFinalEnvelope(ReadOnlySpan<byte> bytes)
    {
        var value = Read(bytes,16384,BattleFinalEnvelope.Parser);
        if (!Uuid(value.ClientRequestId) || value.Manifest.Length is < 1 or > FinalBytes) throw Invalid();
        return value;
    }
    public static BattleTerminalViewV1 ReadTerminalView(ReadOnlySpan<byte> bytes,BattleFinalManifestV1 final)
    {
        var value = Read(bytes,1048576,BattleTerminalViewV1.Parser);
        if (value.Version != 1 || value.WorldInstanceId != final.WorldInstanceId || value.RestoreBoundary != final.RestoreBoundary
            || value.BattleId != final.BattleId || value.Attempt != final.Attempt || value.Fence != final.Fence || !value.StartDigest.Equals(final.StartDigest)
            || value.Sequence != final.TerminalViewSequence || value.TerminalTick != final.TerminalTick || value.HeldTicks != final.HeldTicks
            || value.Outcome != final.Outcome || value.Reason != final.Reason || !value.Pawns.SequenceEqual(final.Pawns) || !Equals(value.Snapshot,final.FinalSnapshot)
            || final.TerminalView.SizeBytes != bytes.Length || !final.TerminalView.Sha256.Span.SequenceEqual(SHA256.HashData(bytes))) throw Invalid();
        return value;
    }
    public static BattleCommittedFinalV1 ReadCommittedFinal(ReadOnlySpan<byte> bytes,BattlePreparedManifestV1 start)
    {
        var value = Read(bytes,16384,BattleCommittedFinalV1.Parser);
        if (value.Version != 1 || value.Transport is null) throw Invalid();
        var envelope = ReadFinalEnvelope(value.ExecutorEnvelope.Span);
        var final = ReadFinal(envelope.Manifest.Span,start); var transport = value.Transport;
        if (transport.Version != 1 || transport.WorldInstanceId != start.WorldInstanceId || transport.RestoreBoundary != start.RestoreBoundary
            || transport.BattleId != start.BattleId || transport.Attempt != start.Attempt || transport.Fence != start.Fence || !transport.StartDigest.Equals(final.StartDigest)
            || !Equals(transport.TerminalView,final.TerminalView) || transport.TerminalViewSequence != final.TerminalViewSequence
            || transport.Watermarks.Count != 2 || transport.Watermarks[0].Side != BattleFighterSide.Attacker || transport.Watermarks[1].Side != BattleFighterSide.Defender
            || transport.Watermarks.Any(w => w.LastSequence is < 0 or > 24000)) throw Invalid();
        RequireDescriptor(transport.Commands,BattleDocumentKind.TransportJournal);
        return value;
    }
    public static BattleFinalManifestV1 ReadFinal(ReadOnlySpan<byte> bytes,BattlePreparedManifestV1 start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var value = Read(bytes,FinalBytes,BattleFinalManifestV1.Parser);
        if (value.Version != 1 || value.WorldInstanceId != start.WorldInstanceId || value.RestoreBoundary != start.RestoreBoundary
            || value.CampaignId != start.CampaignId || value.BattleId != start.BattleId || value.Attempt != start.Attempt || value.Fence != start.Fence
            || value.ExecutorId != start.ExecutorId || value.RuntimeInstanceId != start.RuntimeInstanceId || value.OriginalExecutorLeaseId != start.ExecutorLeaseId
            || value.OriginalExecutorLeaseFence != start.ExecutorLeaseFence || !value.StartNonce.Equals(start.StartNonce) || !value.ProfileDigest.Equals(start.ProfileDigest)
            || !value.StartDigest.Span.SequenceEqual(Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray()))
            || !Equals(value.InitialSnapshot,start.InitialSnapshot) || value.TerminalTick < start.InitialTick
            || (long)value.TerminalTick - start.InitialTick > 30000 || value.HeldTicks < 0 || value.HeldTicks > Math.Min(3000,value.TerminalTick - start.InitialTick)
            || value.WallElapsedMs < 0 || value.TransportLostElapsedMs < 0 || value.TransportLostElapsedMs > value.WallElapsedMs
            || value.TerminalViewSequence <= 0 || value.Pawns.Count != 8 || start.Entities.Count != 8) throw Invalid();
        RequireDescriptor(value.FinalSnapshot,BattleDocumentKind.FinalSnapshot); RequireDescriptor(value.SimulationJournal,BattleDocumentKind.SimulationJournal);
        RequireDescriptor(value.TerminalView,BattleDocumentKind.TerminalView);
        bool attacker = false,defender = false,attackerInZone = false,defenderInZone = false;
        for (int i = 0;i < 8;i++)
        {
            var pawn = value.Pawns[i]; var expected = start.Entities[i];
            if (pawn.FighterId != expected.FighterId || pawn.EntityId != expected.EntityId || pawn.Side != expected.Side || !float.IsFinite(pawn.Health)
                || pawn.Health is < 0 or > 1 || pawn.X < 0 || pawn.Z < 0 || pawn.X >= start.Arena.Width || pawn.Z >= start.Arena.Height) throw Invalid();
            if (!pawn.Alive || pawn.Downed || pawn.Mental || !pawn.CanFight) continue;
            bool inZone = pawn.X >= start.Arena.ZoneMinX && pawn.X <= start.Arena.ZoneMaxX && pawn.Z >= start.Arena.ZoneMinZ && pawn.Z <= start.Arena.ZoneMaxZ;
            if (pawn.Side == BattleFighterSide.Attacker) { attacker = true; attackerInZone |= inZone; }
            else { defender = true; defenderInZone |= inZone; }
        }
        if (value.Outcome == BattleTerminalOutcome.TechnicalAbort)
        {
            bool valid = value.Reason switch {
                BattleTerminalReason.WallClockLimit => value.WallElapsedMs >= 1200000,
                BattleTerminalReason.TransportLost => value.TransportLostElapsedMs >= 60000,
                BattleTerminalReason.InvalidState => true,
                _ => false,
            };
            if (!valid) throw Invalid();
            return value;
        }
        if (value.TerminalTick == start.InitialTick || value.WallElapsedMs >= 1200000
            || value.HeldTicks > 0 && (!attackerInZone || defenderInZone) || value.HeldTicks == 0 && attackerInZone && !defenderInZone) throw Invalid();
        var (outcome,reason) = (!attacker && !defender) ? (BattleTerminalOutcome.Draw,BattleTerminalReason.MutualElimination)
            : !defender ? (BattleTerminalOutcome.AttackerWin,BattleTerminalReason.SideEliminated)
            : !attacker ? (BattleTerminalOutcome.DefenderWin,BattleTerminalReason.SideEliminated)
            : value.HeldTicks == 3000 ? (BattleTerminalOutcome.AttackerWin,BattleTerminalReason.ZoneHeld)
            : value.TerminalTick - start.InitialTick == 30000 ? (BattleTerminalOutcome.DefenderWin,BattleTerminalReason.TickLimit)
            : (BattleTerminalOutcome.Unspecified,BattleTerminalReason.Unspecified);
        if (outcome == BattleTerminalOutcome.Unspecified || value.Outcome != outcome || value.Reason != reason) throw Invalid();
        return value;
    }
}
