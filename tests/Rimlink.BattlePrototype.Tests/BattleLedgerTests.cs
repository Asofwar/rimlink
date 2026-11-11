using System.Security.Cryptography;
using Rimlink.BattlePrototype;
using Xunit;

namespace Rimlink.BattlePrototype.Tests;

public sealed class BattleLedgerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rimlink-battle-prototype-tests", Guid.NewGuid().ToString("N"));
    private static readonly BattleZone Arena = new(0, 0, 99, 99);
    private static readonly BattleZone Zone = new(45, 45, 55, 55);
    private static BattlePawn[] Roster() => Enumerable.Range(1, 4).Select(i => new BattlePawn("a" + i, BattleSide.Attacker, 20, 40 + i))
        .Concat(Enumerable.Range(1, 4).Select(i => new BattlePawn("b" + i, BattleSide.Defender, 80, 40 + i))).ToArray();
    private readonly BattleIdentity identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
        BattleContract.RulesDigest(Arena, Zone), new string('a', 64), BattleContract.Digest(Roster()), 1);

    private BattleArtifact Write(BattleLedger ledger, BattleArtifactKind kind, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(ledger.ArtifactPath(kind), bytes);
        return new(kind, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
    private BattleStart StartData(BattleLedger ledger) => new(identity, Write(ledger, BattleArtifactKind.InitialSnapshot, "initial native snapshot fixture"),
        new(identity, 0, 100, new(100, 0, BattleOutcome.Ongoing, BattleEndReason.None), Arena, Zone, Roster()));
    private BattleFinalResult Final(BattleLedger ledger, BattleStart start, BattleOutcome outcome = BattleOutcome.AttackerWin) => new(identity,
        new(101, 0, outcome, outcome == BattleOutcome.Draw ? BattleEndReason.MutualElimination : BattleEndReason.SideEliminated),
        start.Snapshot.Sha256, Write(ledger, BattleArtifactKind.FinalSnapshot, "final native snapshot fixture"),
        Write(ledger, BattleArtifactKind.Journal, "journal fixture"), Roster().Select(p => outcome == BattleOutcome.Draw || p.Side == (outcome == BattleOutcome.AttackerWin ? BattleSide.Defender : BattleSide.Attacker) ? p with { Alive = false, Health = 0 } : p).ToArray());

    [Theory]
    [InlineData(BattleRole.SideA)]
    [InlineData(BattleRole.SideB)]
    public void APlayerCannotStartOrSubmitAnOutcome(BattleRole role)
    {
        using var ledger = new BattleLedger(root, identity);
        var start = StartData(ledger);
        Assert.Throws<UnauthorizedAccessException>(() => ledger.Start(role, start));
        ledger.Start(BattleRole.Arbiter, start);
        Assert.Throws<UnauthorizedAccessException>(() => ledger.Apply(role, Final(ledger, start)));
        Assert.Equal(0, ledger.State.OwnerChangedCount);
    }

    [Fact]
    public void StartRequiresCompletedArtifactsAndExactRoster()
    {
        using var ledger = new BattleLedger(root, identity);
        var start = StartData(ledger);
        File.Delete(ledger.ArtifactPath(BattleArtifactKind.InitialSnapshot));
        Assert.Throws<InvalidDataException>(() => ledger.Start(BattleRole.Arbiter, start));
        Assert.Equal(BattlePhase.Prepared, ledger.State.Phase);
        start = StartData(ledger);
        Assert.Throws<InvalidDataException>(() => ledger.Start(BattleRole.Arbiter, start with { View = start.View with { Pawns = Roster()[..7] } }));
        ledger.Start(BattleRole.Arbiter, start);
        ledger.Start(BattleRole.Arbiter, start);
        Assert.Equal(BattlePhase.Started, ledger.State.Phase);
    }

    [Fact]
    public void LostAckAndProcessRestartReturnTheSameReceiptOnce()
    {
        BattleFinalResult final;
        BattleReceipt receipt;
        using (var ledger = new BattleLedger(root, identity))
        {
            var start = StartData(ledger); ledger.Start(BattleRole.Arbiter, start);
            final = Final(ledger, start); receipt = ledger.Apply(BattleRole.Arbiter, final);
            Assert.Equal(receipt, ledger.Apply(BattleRole.Arbiter, final));
        }
        using var restarted = new BattleLedger(root, identity);
        Assert.Equal(receipt, restarted.Apply(BattleRole.Arbiter, final));
        Assert.Equal(BattleSide.Attacker, restarted.State.Owner);
        Assert.Equal(1, restarted.State.OwnerChangedCount);
        Assert.Equal(2, restarted.State.OwnerRevision);
    }

    [Theory]
    [InlineData(BattleOutcome.AttackerWin)]
    [InlineData(BattleOutcome.DefenderWin)]
    [InlineData(BattleOutcome.Draw)]
    public void ColdResultDeliveryPublishesTerminalFactsAtomicallyAndReplayKeepsTheView(BattleOutcome outcome)
    {
        using var ledger = new BattleLedger(root, identity);
        var start = StartData(ledger); ledger.Start(BattleRole.Arbiter, start);
        var result = Final(ledger, start, outcome);
        ledger.Apply(BattleRole.Arbiter, result);
        var view = ledger.Poll(BattleRole.SideA, identity).View!;
        Assert.Equal(result.Decision, view.State);
        Assert.Equal(result.Pawns, view.Pawns);
        Assert.True(view.Sequence > start.View.Sequence);
        ledger.Apply(BattleRole.Arbiter, result);
        Assert.Equal(view.Sequence, ledger.Poll(BattleRole.SideB, identity).View!.Sequence);
    }

    [Theory]
    [InlineData(BattleOutcome.DefenderWin)]
    [InlineData(BattleOutcome.Draw)]
    public void RetentionDoesNotInventAnOwnerTransition(BattleOutcome outcome)
    {
        using var ledger = new BattleLedger(root, identity);
        var start = StartData(ledger); ledger.Start(BattleRole.Arbiter, start);
        var receipt = ledger.Apply(BattleRole.Arbiter, Final(ledger, start, outcome));
        Assert.Equal(BattleSide.Defender, receipt.Owner);
        Assert.Equal(0, receipt.OwnerChangedCount);
        Assert.Equal(1, receipt.OwnerRevision);
    }

    [Fact]
    public void ConflictingOutcomeCannotOverwriteAnAcceptedResult()
    {
        using var ledger = new BattleLedger(root, identity);
        var start = StartData(ledger); ledger.Start(BattleRole.Arbiter, start);
        var final = Final(ledger, start); ledger.Apply(BattleRole.Arbiter, final);
        Assert.Throws<InvalidDataException>(() => ledger.Apply(BattleRole.Arbiter, final with { Decision = final.Decision with { Outcome = BattleOutcome.DefenderWin } }));
        Assert.Equal(1, ledger.State.OwnerChangedCount);
    }

    [Fact]
    public void LostViewReplyAllowsOnlyAnExactReplayAndDoesNotAdvanceTheSequence()
    {
        using var ledger = new BattleLedger(root, identity);
        var start = StartData(ledger); ledger.Start(BattleRole.Arbiter, start);
        var view = start.View with { Sequence = 1, State = start.View.State with { Tick = 101 } };
        ledger.Publish(BattleRole.Arbiter, view);
        ledger.Publish(BattleRole.Arbiter, view);
        Assert.Equal(1, ledger.Poll(BattleRole.SideA, identity).View!.Sequence);
        Assert.Throws<InvalidDataException>(() => ledger.Publish(BattleRole.Arbiter, view with { State = view.State with { Tick = 102 } }));
        Assert.Throws<InvalidDataException>(() => ledger.Publish(BattleRole.Arbiter, start.View));
        Assert.Equal(BattlePhase.Started, ledger.State.Phase);
    }

    [Fact]
    public void AValidArtifactCannotCrossAnExecutorOrRestoreFence()
    {
        using var ledger = new BattleLedger(root, identity);
        var start = StartData(ledger); ledger.Start(BattleRole.Arbiter, start);
        var final = Final(ledger, start);
        ledger.RevokeExecutor();
        Assert.Throws<InvalidDataException>(() => ledger.Apply(BattleRole.Arbiter, final));
        ledger.RotateBoundary(Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => ledger.Apply(BattleRole.Arbiter, final));
        Assert.Equal(0, ledger.State.OwnerChangedCount);
    }

    [Fact]
    public void WrongBattleOrIdentityNeverChangesTheOwner()
    {
        using var ledger = new BattleLedger(root, identity);
        var start = StartData(ledger); ledger.Start(BattleRole.Arbiter, start);
        var final = Final(ledger, start);
        Assert.Throws<InvalidDataException>(() => ledger.Apply(BattleRole.Arbiter, final with { Identity = identity with { BattleId = Guid.NewGuid() } }));
        Assert.Throws<InvalidDataException>(() => ledger.Apply(BattleRole.Arbiter, final with { Identity = identity with { RulesDigest = new string('b', 64) } }));
        Assert.Equal(0, ledger.State.OwnerChangedCount);
    }

    [Fact]
    public void MissingFinalFileIsRecoveryNotASuccessOrTransfer()
    {
        using var ledger = new BattleLedger(root, identity);
        var start = StartData(ledger); ledger.Start(BattleRole.Arbiter, start);
        var final = Final(ledger, start);
        File.Delete(ledger.ArtifactPath(BattleArtifactKind.FinalSnapshot));
        Assert.Throws<InvalidDataException>(() => ledger.Apply(BattleRole.Arbiter, final));
        Assert.Equal(BattlePhase.RecoveryRequired, ledger.State.Phase);
        Assert.Equal(0, ledger.State.OwnerChangedCount);
    }

    [Fact]
    public void CorruptAcceptedEvidenceIsDetectedAfterRestart()
    {
        using (var ledger = new BattleLedger(root, identity))
        {
            var start = StartData(ledger); ledger.Start(BattleRole.Arbiter, start);
            ledger.Apply(BattleRole.Arbiter, Final(ledger, start));
            File.AppendAllText(ledger.ArtifactPath(BattleArtifactKind.Journal), "corruption");
        }
        using var reopened = new BattleLedger(root, identity);
        Assert.Equal(BattlePhase.RecoveryRequired, reopened.State.Phase);
        Assert.Equal(1, reopened.State.OwnerChangedCount);
    }

    public void Dispose()
    {
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rimlink-battle-prototype-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
            throw new InvalidOperationException("test cleanup escaped its own root");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
