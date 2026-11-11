using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleExecutorHelloServiceTests
{
    private static BattleExecutorHelloService Service(GuildFixture f)=>new(f.Executors,new(f.Time,f.ExecutorBoot),new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Profiles,f.Artifacts,f.Clock);
    private static BattleExecutorHelloComplete Complete(BattleExecutorHelloBindingV1 binding,BattleExecutorHelloChallenge challenge)=>new(){Binding=binding.Clone(),ChallengeId=challenge.ChallengeId,Nonce=challenge.Nonce,ServerBootId=challenge.ServerBootId};
    [Fact]
    public async Task Idle_hello_checks_the_approved_measurement_and_never_creates_financial_history()
    {
        using var f=await GuildFixture.Create(true);await f.EnsureApprovedProfile();var service=Service(f);var binding=f.ExecutorBinding();string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        var begin=await service.BeginAsync(binding,"credential",default);var complete=Complete(binding,begin);
        var lease=await service.CompleteAsync(complete,"credential",default);Assert.Equal(lease,await service.CompleteAsync(complete,"credential",default));
        Assert.Equal(binding,lease.Binding);Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        binding.ExecutorBuildSha256=Google.Protobuf.ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData("wrong build"u8));await Assert.ThrowsAnyAsync<Exception>(()=>service.BeginAsync(binding,"credential",default));
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("cancelled")]
    [InlineData("foreign-runtime")]
    [InlineData("initial-upload")]
    public async Task Entry_after_server_restart_can_recover_only_the_original_assignment_without_started_work(string scenario)
    {
        using var f = await GuildFixture.Create(true);
        var (original, prepared) = await BattleExecutorWorkTests.Prepare(f);
        var binding = f.Executors.BindingOf(original)!;
        var assignment = prepared.RuntimeAssignment;
        if (scenario == "cancelled")
        {
            f.Time.Advance(TimeSpan.FromMinutes(5));
            await f.Service.ExpireCampaignAsync(f.Scope, Guid.NewGuid().ToString("D"), assignment.CampaignId, default);
        }
        if (scenario == "foreign-runtime") binding.RuntimeInstanceId = Guid.NewGuid().ToString("D");
        if (scenario == "initial-upload")
        {
            var bytes = new byte[] { 1, 2 };
            string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            using var storage = await f.H.Files.PerStoreLock.HoldAsync(default);
            using var battle = await f.Battles.HoldAsync(default);
            f.Artifacts.AppendHeld(new(1, assignment.BattleId, f.Scope.WorldId, f.Scope.Boundary, assignment.ExecutorId,
                assignment.Fence, "initial", bytes.Length, digest), 0, bytes.AsSpan(0, 1));
        }

        var restarted = new BattleExecutorRegistry(f.Time, assignment.ExecutorId, Convert.FromHexString(assignment.ProfileDigest));
        var admission = new BattleTransportAdmission(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, f.Battles);
        var service = new BattleExecutorHelloService(restarted, new(f.Time, Guid.NewGuid().ToString("D")), admission, f.Profiles, f.Artifacts, f.Clock);
        if (scenario is "foreign-runtime" or "initial-upload")
        {
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => service.BeginAsync(binding, "credential", default));
            return;
        }

        string head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        var challenge = await service.BeginAsync(binding, "credential", default);
        var lease = await service.CompleteAsync(Complete(binding, challenge), "credential", default);
        var work = await new BattleExecutorWorkService(restarted, admission, f.Clock, f.Artifacts).PollAsync(BattleResultStaging.Lease(lease), default);
        Assert.NotEqual(original.LeaseId, lease.LeaseId);
        Assert.Equal(assignment.Message(), scenario == "cancelled" ? work.Cancelled?.Assignment : work.Preparing?.Assignment);
        Assert.Equal(head, (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
    }
    [Theory]
    [InlineData("valid")][InlineData("missing")][InlineData("unloaded")][InlineData("unknown-journal")]
    public async Task Only_the_original_live_process_with_retained_native_context_can_reconnect_for_execution(string damage)
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,true);await f.Service.StartBattleAsync(request,default);
        var original=BattleResultStaging.Lease(request.Executor);var binding=f.Executors.BindingOf(original)!;
        binding.Entry=null;binding.BattleId=start.BattleId;binding.Attempt=start.Attempt;binding.Fence=start.Fence;binding.AssignmentTransitionId=start.PreparationTransitionId;binding.StartDigest=request.ManifestDigest;
        binding.RetainedContext=new(){Version=1,Phase=BattleExecutorRetainedPhase.Running,StartNonce=start.StartNonce,GameLoaded=true,TickRuntimeBound=true,
            CurrentTick=start.InitialTick,ReceiptJournalSha256=Google.Protobuf.ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData("retained native receipts"u8)),ReceiptJournalBytes=40};
        switch(damage){case "missing":binding.RetainedContext=null;break;case "unloaded":binding.RetainedContext.GameLoaded=false;break;
            case "unknown-journal":binding.RetainedContext.ReceiptJournalSha256=Google.Protobuf.ByteString.Empty;binding.RetainedContext.ReceiptJournalBytes=0;break;}
        var service=Service(f);
        if(damage=="valid")
        {
            var challenge=await service.BeginAsync(binding,"credential",default);var next=await service.CompleteAsync(Complete(binding,challenge),"credential",default);
            Assert.NotEqual(original.LeaseId,next.LeaseId);Assert.Equal(start.RuntimeInstanceId,next.Binding.RuntimeInstanceId);
            Assert.Throws<InvalidDataException>(()=>f.Executors.RequireCurrent(original));Assert.Equal(start.RuntimeInstanceId,f.Executors.BindingOf(BattleResultStaging.Lease(next))!.RuntimeInstanceId);
        }
        else await Assert.ThrowsAnyAsync<Exception>(()=>service.BeginAsync(binding,"credential",default));
    }
    [Fact]
    public async Task Historical_profile_delivery_survives_config_rotation_and_revocation_without_reopening_execution()
    {
        using var f=await GuildFixture.Create(true);var(executor,envelope,start)=await GuildBattleResultTests.Prepare(f);
        var binding=f.Executors.BindingOf(executor)!;binding.RuntimeInstanceId=Guid.NewGuid().ToString("D");binding.Lane=BattleExecutorLane.RecoveryDelivery;
        binding.Entry=null;binding.BattleId=start.BattleId;binding.Attempt=start.Attempt;binding.Fence=start.Fence;binding.StartDigest=requestDigest(start);binding.AssignmentTransitionId=start.PreparationTransitionId;
        await f.Service.RevokeBattleProfileAsync(Rimlink.Server.Application.Admin.Actor.Api,f.Scope,Guid.NewGuid().ToString("D"),start.ApprovalTransitionId,"withdrawn fixture",default);
        f.UseExecutorProfile(System.Security.Cryptography.SHA256.HashData("new configured profile"u8));var service=Service(f);
        var challenge=await service.BeginAsync(binding,"current credential",default);var lease=await service.CompleteAsync(Complete(binding,challenge),"current credential",default);
        Assert.Equal(60,(await f.Service.ApplyBattleFinalAsync(BattleResultStaging.Lease(lease),envelope,default)).Receipt.CurrentAllianceId);Assert.Null(f.Executors.Available());
        static Google.Protobuf.ByteString requestDigest(BattlePreparedManifestV1 start)=>Google.Protobuf.ByteString.CopyFrom(Rimlink.Protocol.BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,Google.Protobuf.MessageExtensions.ToByteArray(start)));
    }
    [Fact]
    public async Task A_courier_cannot_assert_SameProcessEntry_for_the_original_runtime()
    {
        using var f=await GuildFixture.Create(true);var(executor,envelope,start)=await GuildBattleResultTests.Prepare(f);await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        var courier=f.RegisterRecovery(start);var scope=new BattleScopeMessage{WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,BattleId=start.BattleId,Attempt=start.Attempt,Fence=start.Fence};
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.CleanupChallengeAsync(courier,scope,default));
        Assert.True((await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildBattle>().AsNoTracking().SingleAsync()).Active);
    }
    [Fact]
    public async Task A_new_process_cannot_get_execution_for_an_existing_assignment_but_a_scoped_courier_can_deliver()
    {
        using var f=await GuildFixture.Create(true);var(request,start)=await GuildBattleStartTests.Prepare(f,true);await f.Service.StartBattleAsync(request,default);
        var original=BattleResultStaging.Lease(request.Executor);var service=Service(f);var binding=f.ExecutorBinding();binding.Entry=null;binding.BattleId=start.BattleId;binding.Attempt=start.Attempt;binding.Fence=start.Fence;
        binding.AssignmentTransitionId=start.PreparationTransitionId;binding.StartDigest=request.ManifestDigest;
        await Assert.ThrowsAnyAsync<Exception>(()=>service.BeginAsync(binding,"credential",default));
        binding.Lane=BattleExecutorLane.RecoveryDelivery;var challenge=await service.BeginAsync(binding,"credential",default);var delivery=await service.CompleteAsync(Complete(binding,challenge),"credential",default);
        f.Executors.RequireCurrent(original);Assert.Equal(BattleExecutorLane.RecoveryDelivery,delivery.Binding.Lane);Assert.NotEqual(start.RuntimeInstanceId,delivery.Binding.RuntimeInstanceId);
        await Assert.ThrowsAnyAsync<Exception>(()=>new BattleExecutorWorkService(f.Executors,new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Clock,f.Artifacts).PollAsync(BattleResultStaging.Lease(delivery),default));
    }
}
