using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleRuntimeAssignmentTests
{
    [Fact]
    public async Task Common_preparation_pins_the_process_before_first_work_and_keeps_physical_occupancy()
    {
        using var f=await GuildFixture.Create(true);var(executor,prepared)=await BattleExecutorWorkTests.Prepare(f);
        var binding=f.Executors.BindingOf(executor)!;var assignment=prepared.RuntimeAssignment;
        Assert.Equal(binding.RuntimeInstanceId,assignment.RuntimeInstanceId);Assert.Equal(executor.LeaseId,assignment.OriginalLeaseId);
        Assert.Equal(prepared.BattleId,assignment.BattleId);Assert.Equal(prepared.StartNonce,assignment.StartNonce);Assert.Equal(prepared.TransitionId,assignment.TransitionId);
        Assert.True((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Active);
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Fact]
    public async Task Reconnect_after_Start_replays_the_original_Start_authorization_without_rewriting_its_execution_identity()
    {
        using var f=await GuildFixture.Create(true);var(request,manifest)=await GuildBattleStartTests.Prepare(f,true);var original=await f.Service.StartBattleAsync(request,default);
        var executor=BattleResultStaging.Lease(request.Executor);var next=f.Executors.RegisterExecutionAuthenticated(f.Executors.BindingOf(executor)!,f.Executors.Wire(executor).ServerBootId);
        request.Executor=f.Executors.Wire(next);Assert.Equal(original,await f.Service.StartBattleAsync(request,default));
        var committed=(await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).StartManifest!;
        Assert.Equal(manifest.ExecutorLeaseId,Rimlink.Protocol.BattleManifestContracts.ReadPrepared(committed).ExecutorLeaseId);
    }
    [Fact]
    public async Task Same_process_reconnect_preserves_the_original_assignment_and_the_initial_work_identity()
    {
        using var f=await GuildFixture.Create(true);var(executor,prepared)=await BattleExecutorWorkTests.Prepare(f);var binding=f.Executors.BindingOf(executor)!;
        var next=f.Executors.RegisterExecutionAuthenticated(binding,Guid.Parse(f.Executors.Wire(executor).ServerBootId).ToString("D"));
        var admission=new BattleTransportAdmission(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles);
        var work=await new BattleExecutorWorkService(f.Executors,admission,f.Clock,f.Artifacts).PollAsync(next,default);
        Assert.Equal(executor.LeaseId,work.Preparing.Assignment.OriginalLeaseId);Assert.Equal(binding.RuntimeInstanceId,work.Preparing.Assignment.RuntimeInstanceId);
        Assert.Equal(prepared.RuntimeAssignment,(await GuildAuthorityEvidence.PreparationAsync(f.H.Db,prepared.BattleId,default)).RuntimeAssignment);
    }
}
