using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleMapGuardTests
{
    private static BattleScopeMessage Scope() => new() { WorldInstanceId = "world",RestoreBoundary = Guid.NewGuid().ToString("D"),BattleId = Guid.NewGuid().ToString("D"),Attempt = 1,Fence = 1 };
    private static GuildSnapshotProof Home(BattleScopeMessage scope) => new() { Scope = new() { WorldInstanceId = scope.WorldInstanceId,RestoreBoundary = scope.RestoreBoundary,
        PlayerId = 10,SaveGeneration = 2,SaveWriteEpoch = 3 },SnapshotId = Guid.NewGuid().ToString("N"),RawSha256 = ByteString.CopyFrom(new byte[32]) };
    [Fact]
    public void Viewer_is_foreign_before_loading_and_remains_foreign_until_the_exact_canonical_home_is_proved()
    {
        var guard = new BattleMapGuard(); var scope = Scope(); var home = Home(scope); var expected = home.Clone();
        guard.BeginViewer(scope,home,7);
        scope.Fence++; home.Scope.SaveGeneration++;
        Assert.True(guard.Active); Assert.False(guard.MayUseOwnGame); Assert.Equal(BattleMapRole.Viewer,guard.Role);
        Assert.Equal(1,guard.Scope!.Fence); Assert.Equal(2,guard.Home!.Scope.SaveGeneration);
        var wrong = expected.Clone(); wrong.RawSha256 = ByteString.CopyFrom(new byte[31]);
        Assert.Throws<InvalidDataException>(() => guard.RestoreViewerHome(wrong,7));
        Assert.Throws<InvalidDataException>(() => guard.RestoreViewerHome(expected,8));
        Assert.True(guard.Active);
        guard.RestoreViewerHome(expected,7);
        Assert.False(guard.Active); Assert.True(guard.MayUseOwnGame); Assert.Null(guard.Scope); Assert.Null(guard.Home);
    }
    [Theory]
    [InlineData("world")][InlineData("boundary")][InlineData("hash")][InlineData("snapshot")][InlineData("player")][InlineData("version")]
    public void An_unverified_home_never_allows_a_battle_handoff(string damage)
    {
        var guard = new BattleMapGuard(); var scope = Scope(); var home = Home(scope); int version = 7;
        switch (damage) { case "world": home.Scope.WorldInstanceId = "other"; break; case "boundary": home.Scope.RestoreBoundary = Guid.NewGuid().ToString("D"); break;
            case "hash": home.RawSha256 = ByteString.Empty; break; case "snapshot": home.SnapshotId = ""; break; case "player": home.Scope.PlayerId = 0; break; case "version": version = 0; break; }
        Assert.Throws<InvalidDataException>(() => guard.BeginViewer(scope,home,version));
        Assert.False(guard.Active);
    }
    [Fact]
    public void A_context_cannot_be_nested_or_replaced_by_another_match()
    {
        var scope = Scope(); var guard = new BattleMapGuard(); guard.BeginViewer(scope,Home(scope),7);
        Assert.Throws<InvalidOperationException>(() => guard.BeginExecutor(Scope()));
        Assert.Throws<InvalidOperationException>(() => guard.BeginViewer(scope,Home(scope),7));
        Assert.Equal(scope,guard.Scope);
    }
    [Theory]
    [InlineData(BattleCleanupTerminalKind.AppliedResult)][InlineData(BattleCleanupTerminalKind.PreStartCancellation)]
    public void Executor_has_no_personal_home_and_only_matching_committed_cleanup_ends_the_context(BattleCleanupTerminalKind kind)
    {
        var scope = Scope(); var guard = new BattleMapGuard(); guard.BeginExecutor(scope);
        Assert.Equal(BattleMapRole.Executor,guard.Role); Assert.False(guard.MayUseOwnGame); Assert.Null(guard.Home);
        var clean = new BattleCleanupReceipt { Version = 1,Scope = scope.Clone(),TransitionId = Guid.NewGuid().ToString("D"),
            RuntimeInstanceId = Guid.NewGuid().ToString("D"),CommittedFinalDigest = kind==BattleCleanupTerminalKind.AppliedResult?ByteString.CopyFrom(new byte[32]):ByteString.Empty,CleanedAtUtcTicks = DateTime.UtcNow.Ticks,ProofKind=BattleCleanupProofKind.SameProcessEntry };
        clean.Terminal=new(){Version=1,Kind=kind,TerminalTransitionId=Guid.NewGuid().ToString("D"),AssignmentTransitionId=Guid.NewGuid().ToString("D"),CommittedFinalDigest=clean.CommittedFinalDigest};
        var wrong = clean.Clone(); wrong.Scope.Fence++;
        Assert.Throws<InvalidDataException>(() => guard.CompleteExecutor(wrong)); Assert.True(guard.Active);
        guard.CompleteExecutor(clean); Assert.False(guard.Active);
    }
}
