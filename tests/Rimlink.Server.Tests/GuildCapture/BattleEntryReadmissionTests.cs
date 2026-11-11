using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleEntryReadmissionTests
{
    private static BattleExecutorHelloService Hello(GuildFixture f)=>new(f.Executors,new(f.Time),new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Profiles,f.Artifacts,f.Clock);
    private static async Task<BattleExecutorLeaseMessage> Register(GuildFixture f,BattleExecutorHelloBindingV1 binding)
    {var hello=Hello(f);var challenge=await hello.BeginAsync(binding,"credential",default);return await hello.CompleteAsync(new(){Binding=binding,ChallengeId=challenge.ChallengeId,Nonce=challenge.Nonce,ServerBootId=challenge.ServerBootId},"credential",default);}
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Fresh_Entry_can_replace_a_stale_projection_only_after_all_original_assignments_have_common_Cleaned_proof(bool cancelled)
    {
        using var f=await GuildFixture.Create(true);BattleExecutorLease executor;BattleScopeMessage scope;
        if(cancelled)
        {
            var(request,start)=await GuildBattleStartTests.Prepare(f,false);executor=BattleResultStaging.Lease(request.Executor);var binding=f.Executors.BindingOf(executor)!;
            f.Time.Advance(TimeSpan.FromMinutes(5));executor=f.Executors.RegisterExecutionAuthenticated(binding,f.ExecutorBoot);
            await f.Service.ExpireCampaignAsync(f.Scope,Guid.NewGuid().ToString("D"),start.CampaignId,default);scope=request.Scope;
        }
        else
        {var(lease,envelope,_)=await GuildBattleResultTests.Prepare(f);executor=lease;var applied=await f.Service.ApplyBattleFinalAsync(executor,envelope,default);scope=GuildBattleCleanupTests.Scope(applied.Receipt);}
        var challenge=await f.Service.CleanupChallengeAsync(executor,scope,default);var cleanup=GuildBattleCleanupTests.Request(executor,challenge);
        await f.Service.CleanupBattleAsync(executor,cleanup.ClientRequestId,cleanup.Proof,default);
        f.Executors.HoldCleanup(executor);Assert.Null(f.Executors.Available());
        var admitted=await Register(f,f.ExecutorBinding());Assert.Equal(BattleResultStaging.Lease(admitted),f.Executors.Available());
        Assert.Throws<InvalidDataException>(()=>f.Executors.RequireCurrent(executor));
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.CleanupBattleAsync(BattleResultStaging.Lease(admitted),cleanup.ClientRequestId,cleanup.Proof,default));
        Assert.Equal(BattleResultStaging.Lease(admitted),f.Executors.Available());
    }
    [Fact]
    public async Task A_hold_without_any_common_Cleaned_assignment_cannot_be_erased_by_a_new_Entry_hello()
    {
        using var f=await GuildFixture.Create(true);await f.EnsureApprovedProfile();var executor=f.RegisterExecutor();f.Executors.HoldCleanup(executor);
        await Assert.ThrowsAnyAsync<Exception>(()=>Register(f,f.ExecutorBinding()));Assert.Null(f.Executors.Available());
    }
    [Fact]
    public async Task A_precancel_cleanup_receipt_lost_across_server_restart_can_be_delivered_without_a_fake_Start_or_Final()
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,false);var executor=BattleResultStaging.Lease(request.Executor);
        var binding=f.Executors.BindingOf(executor)!;f.Time.Advance(TimeSpan.FromMinutes(5));executor=f.Executors.RegisterExecutionAuthenticated(binding,f.ExecutorBoot);
        await f.Service.ExpireCampaignAsync(f.Scope,Guid.NewGuid().ToString("D"),start.CampaignId,default);
        var challenge=await f.Service.CleanupChallengeAsync(executor,request.Scope,default);var cleanup=GuildBattleCleanupTests.Request(executor,challenge);
        var receipt=await f.Service.CleanupBattleAsync(executor,cleanup.ClientRequestId,cleanup.Proof,default);f.UseExecutorProfile(f.ExpectedProfile);
        binding.Entry=null;binding.Lane=BattleExecutorLane.RecoveryDelivery;binding.BattleId=start.BattleId;binding.Attempt=start.Attempt;binding.Fence=start.Fence;binding.AssignmentTransitionId=start.PreparationTransitionId;
        var courier=await Register(f,binding);
        Assert.Equal(receipt,await f.Service.CleanupBattleAsync(BattleResultStaging.Lease(courier),cleanup.ClientRequestId,cleanup.Proof,default));Assert.Null(f.Executors.Available());
    }
}
