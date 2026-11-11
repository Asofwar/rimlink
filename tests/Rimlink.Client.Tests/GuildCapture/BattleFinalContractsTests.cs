using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleFinalContractsTests
{
    private static BattleFinalManifestV1 Final(BattlePreparedManifestV1 start)
    {
        var value = new BattleFinalManifestV1 { Version = 1,WorldInstanceId = start.WorldInstanceId,RestoreBoundary = start.RestoreBoundary,
            CampaignId = start.CampaignId,BattleId = start.BattleId,Attempt = start.Attempt,Fence = start.Fence,ExecutorId = start.ExecutorId,
            RuntimeInstanceId = start.RuntimeInstanceId,OriginalExecutorLeaseId = start.ExecutorLeaseId,OriginalExecutorLeaseFence = start.ExecutorLeaseFence,
            StartNonce = start.StartNonce,StartDigest = ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray())),
            ProfileDigest = start.ProfileDigest,InitialSnapshot = start.InitialSnapshot.Clone(),
            FinalSnapshot = new() { Kind = BattleDocumentKind.FinalSnapshot,Sha256 = start.InitialSnapshot.Sha256,SizeBytes = 1 },
            SimulationJournal = new() { Kind = BattleDocumentKind.SimulationJournal,Sha256 = start.InitialSnapshot.Sha256,SizeBytes = 1 },
            TerminalView = new() { Kind = BattleDocumentKind.TerminalView,Sha256 = start.InitialSnapshot.Sha256,SizeBytes = 1 },
            TerminalTick = start.InitialTick + 3000,HeldTicks = 3000,Outcome = BattleTerminalOutcome.AttackerWin,Reason = BattleTerminalReason.ZoneHeld,
            WallElapsedMs = 50000,TerminalViewSequence = 1 };
        foreach (var fighter in start.Entities) value.Pawns.Add(new BattleTerminalPawn { FighterId = fighter.FighterId,EntityId = fighter.EntityId,
            Side = fighter.Side,X = fighter.Side == BattleFighterSide.Attacker ? 25 : 40,Z = 25,Alive = true,CanFight = true,Health = 1 });
        return value;
    }
    [Fact]
    public void Exact_terminal_objective_is_valid_and_canonical_and_its_digest_is_not_a_raw_hash()
    {
        var start = BattleManifestContractsTests.Manifest(); var final = Final(start);
        Assert.Equal(final,BattleManifestContracts.ReadFinal(final.ToByteArray(),start));
        Assert.Equal(32,BattleManifestContracts.Digest(BattleDocumentKind.FinalManifest,final.ToByteArray()).Length);
    }
    [Theory]
    [InlineData("missing")][InlineData("identity")][InlineData("side")][InlineData("health")][InlineData("contested")][InlineData("held")][InlineData("tick")]
    [InlineData("world")][InlineData("profile")][InlineData("lease")][InlineData("nonce")][InlineData("initial")][InlineData("start")][InlineData("wall")][InlineData("outcome")]
    public void A_partial_foreign_or_nonterminal_claim_never_becomes_a_capture(string damage)
    {
        var start = BattleManifestContractsTests.Manifest(); var final = Final(start);
        switch (damage) {
            case "missing": final.Pawns.RemoveAt(7); break;
            case "identity": final.Pawns[7].EntityId = final.Pawns[0].EntityId; break;
            case "side": final.Pawns[0].Side = BattleFighterSide.Defender; break;
            case "health": final.Pawns[0].Health = float.NaN; break;
            case "contested": final.Pawns[4].X = 25; break;
            case "held": final.HeldTicks = 2999; break;
            case "tick": final.TerminalTick = start.InitialTick + 2999; break;
            case "world": final.RestoreBoundary = Guid.NewGuid().ToString("D"); break;
            case "profile": final.ProfileDigest = ByteString.CopyFrom(new byte[32]); break;
            case "lease": final.OriginalExecutorLeaseId = Guid.NewGuid().ToString("D"); break;
            case "nonce": final.StartNonce = ByteString.CopyFrom(new byte[32]); break;
            case "initial": final.InitialSnapshot.SizeBytes++; break;
            case "start": final.StartDigest = ByteString.CopyFrom(new byte[32]); break;
            case "wall": final.WallElapsedMs = 1200000; break;
            case "outcome": final.Outcome = BattleTerminalOutcome.DefenderWin; break;
        }
        Assert.Throws<InvalidDataException>(() => BattleManifestContracts.ReadFinal(final.ToByteArray(),start));
    }
    [Theory]
    [InlineData("mutual",BattleTerminalOutcome.Draw,BattleTerminalReason.MutualElimination)]
    [InlineData("defender-lost",BattleTerminalOutcome.AttackerWin,BattleTerminalReason.SideEliminated)]
    [InlineData("attacker-lost",BattleTerminalOutcome.DefenderWin,BattleTerminalReason.SideEliminated)]
    [InlineData("deadline",BattleTerminalOutcome.DefenderWin,BattleTerminalReason.TickLimit)]
    public void Only_the_actual_terminal_predicates_determine_the_outcome(string scenario,BattleTerminalOutcome outcome,BattleTerminalReason reason)
    {
        var start = BattleManifestContractsTests.Manifest(); var final = Final(start); final.HeldTicks = 0;
        final.TerminalTick = start.InitialTick + (scenario == "deadline" ? 30000 : 1);
        foreach (var pawn in final.Pawns)
        {
            pawn.X = 40;
            if (scenario == "mutual" || scenario == "defender-lost" && pawn.Side == BattleFighterSide.Defender
                || scenario == "attacker-lost" && pawn.Side == BattleFighterSide.Attacker) pawn.Downed = true;
        }
        final.Outcome = outcome; final.Reason = reason;
        Assert.Equal(outcome,BattleManifestContracts.ReadFinal(final.ToByteArray(),start).Outcome);
        final.Outcome = outcome == BattleTerminalOutcome.AttackerWin ? BattleTerminalOutcome.DefenderWin : BattleTerminalOutcome.AttackerWin;
        Assert.Throws<InvalidDataException>(() => BattleManifestContracts.ReadFinal(final.ToByteArray(),start));
    }
    [Theory]
    [InlineData(BattleTerminalReason.WallClockLimit,1200000,0)]
    [InlineData(BattleTerminalReason.TransportLost,60000,60000)]
    [InlineData(BattleTerminalReason.InvalidState,0,0)]
    public void A_technical_terminal_is_explicit_and_never_an_automatic_defender_victory(BattleTerminalReason reason,long wall,long lost)
    {
        var start = BattleManifestContractsTests.Manifest(); var final = Final(start); final.TerminalTick = start.InitialTick; final.HeldTicks = 0;
        final.Outcome = BattleTerminalOutcome.TechnicalAbort; final.Reason = reason; final.WallElapsedMs = wall; final.TransportLostElapsedMs = lost;
        Assert.Equal(BattleTerminalOutcome.TechnicalAbort,BattleManifestContracts.ReadFinal(final.ToByteArray(),start).Outcome);
        if (reason != BattleTerminalReason.InvalidState)
        {
            if (reason == BattleTerminalReason.WallClockLimit) final.WallElapsedMs--; else final.TransportLostElapsedMs--;
            Assert.Throws<InvalidDataException>(() => BattleManifestContracts.ReadFinal(final.ToByteArray(),start));
        }
    }
}
