using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildBattleCleanupTests
{
    [Fact]
    public async Task Only_a_fresh_clean_runtime_proof_releases_the_physical_slot_without_reapplying_capture()
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,start) = await GuildBattleResultTests.Prepare(f);
        var applied = await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        var challenge = await f.Service.CleanupChallengeAsync(executor,Scope(applied.Receipt),default);
        Assert.Null(f.Executors.Available());
        var request = Request(executor,challenge);
        var first = await f.Service.CleanupBattleAsync(executor,request.ClientRequestId,request.Proof,default);
        Assert.Equal(first,await f.Service.CleanupBattleAsync(executor,request.ClientRequestId,request.Proof,default));
        var battle = await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync(); Assert.False(battle.Active); Assert.Equal("Cleaned",battle.Phase);
        Assert.Equal(executor,f.Executors.Available());
        Assert.Equal(applied,await f.Service.ApplyBattleFinalAsync(executor,envelope,default));
        Assert.Single(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());
        Assert.Equal(2,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Revision);
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Theory]
    [InlineData("nonce")][InlineData("runtime")][InlineData("inventory")][InlineData("game")][InlineData("map")][InlineData("ticks")][InlineData("home")][InlineData("entry")][InlineData("expired")][InlineData("result")]
    public async Task Unproved_cleanup_never_frees_an_uncertain_process_or_rolls_back_an_awarded_result(string damage)
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,_) = await GuildBattleResultTests.Prepare(f);
        var applied = await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        var request = Request(executor,await f.Service.CleanupChallengeAsync(executor,Scope(applied.Receipt),default));
        switch (damage) {
            case "nonce": request.Proof.Nonce = ByteString.CopyFrom(new byte[32]); break;
            case "runtime": request.Proof.RuntimeInstanceId = Guid.NewGuid().ToString("D"); break;
            case "inventory": request.Proof.InventorySha256 = ByteString.CopyFrom(new byte[32]); break;
            case "game": request.Proof.GameLoaded = true; break;
            case "map": request.Proof.BattleMapLoaded = true; break;
            case "ticks": request.Proof.TickRuntimeBound = true; break;
            case "home": request.Proof.HomeMarkerLoaded = true; break;
            case "entry": request.Proof.AtEntry = false; break;
            case "expired": for (int i = 0;i < 3;i++) { f.Time.Advance(TimeSpan.FromSeconds(20)); f.Executors.HeartbeatAuthenticated(executor,false); } f.Time.Advance(TimeSpan.FromSeconds(1)); break;
            case "result": request.Proof.CommittedFinalDigest = ByteString.CopyFrom(new byte[32]); break;
        }
        var error = await Record.ExceptionAsync(() => f.Service.CleanupBattleAsync(executor,request.ClientRequestId,request.Proof,default));
        Assert.True(error is InvalidDataException or OutpostEvidenceUnavailableException,error?.ToString() ?? "invalid cleanup unexpectedly accepted");
        Assert.True((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Active);
        Assert.Equal(60,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).AllianceId);
        Assert.Null(f.Executors.Available());
    }
    [Theory]
    [InlineData("BeforePending",false)][InlineData("AfterPending",false)][InlineData("AfterDomainCommit",true)][InlineData("AfterExternalCommit",true)]
    public async Task Lost_cleanup_reply_reconciles_only_the_operational_barrier(string point,bool committed)
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,_) = await GuildBattleResultTests.Prepare(f);
        var applied = await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        var request = Request(executor,await f.Service.CleanupChallengeAsync(executor,Scope(applied.Receipt),default));
        f.H.Authority.CommitCheckpoint = checkpoint => { if (checkpoint == point) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => f.Service.CleanupBattleAsync(executor,request.ClientRequestId,request.Proof,default));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(!committed,(await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Active);
        var replay = await f.Service.CleanupBattleAsync(executor,request.ClientRequestId,request.Proof,default);
        Assert.Equal(replay,await f.Service.CleanupBattleAsync(executor,request.ClientRequestId,request.Proof,default));
        Assert.Equal(applied,await f.Service.ApplyBattleFinalAsync(executor,envelope,default));
    }
    [Fact]
    public async Task A_fresh_registry_after_restart_does_not_advertise_capacity_over_an_unclean_physical_process()
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,start) = await GuildBattleResultTests.Prepare(f);
        await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        f.UseExecutorProfile(f.ExpectedProfile); f.RegisterExecutor();
        Assert.NotNull(f.Executors.Available());
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.DeclareCampaignAsync(1,
            new(f.Scope,Guid.NewGuid().ToString("D"),50,start.OutpostId,2,1),default));
        Assert.True((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Active);
        Assert.Equal(60,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).AllianceId);
    }
    private sealed class Interrupted : Exception;
    internal static BattleScopeMessage Scope(BattleAppliedReceipt receipt) => new() { WorldInstanceId = receipt.WorldInstanceId,RestoreBoundary = receipt.RestoreBoundary,
        BattleId = receipt.BattleId,Attempt = receipt.Attempt,Fence = receipt.Fence };
    internal static BattleCleanupRequest Request(BattleExecutorLease executor,BattleCleanupChallengeMessage challenge) => new() {
        ClientRequestId = Guid.NewGuid().ToString("D"),Executor = new() { ExecutorId = executor.ExecutorId,LeaseId = executor.LeaseId,Fence = executor.Fence,ProfileDigest = executor.ProfileDigest },
        Proof = new() { Version = 1,ChallengeId = challenge.ChallengeId,Nonce = challenge.Nonce,Scope = challenge.Scope.Clone(),CommittedFinalDigest = challenge.CommittedFinalDigest,
            RuntimeInstanceId = challenge.RuntimeInstanceId,InventorySha256 = challenge.InventorySha256,AtEntry = true,Terminal=challenge.Terminal.Clone(),AssignmentRejected=true,ProofKind=BattleCleanupProofKind.SameProcessEntry } };
}
