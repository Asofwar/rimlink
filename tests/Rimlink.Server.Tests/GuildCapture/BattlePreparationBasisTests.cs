using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattlePreparationBasisTests
{
    [Fact]
    public async Task Basis_supplies_all_server_facts_without_a_financial_write_and_accepts_only_the_actual_native_fields()
    {
        using var f=await GuildFixture.Create(true);var(service,executor,manifest)=await BattlePreparedAdmissionTests.Prepare(f);
        var request=Request(executor,manifest);string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        BattlePreparationBasisReply reply;
        using(await f.H.Anchor.AcquireAsync(default))
        {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));reply=await service.ReadBasisAsync(request,timeout.Token);Assert.Equal(reply,await service.ReadBasisAsync(request,timeout.Token));}
        Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Null(reply.Basis.InitialSnapshot);Assert.Empty(reply.Basis.Entities);Assert.Equal(0,reply.Basis.InitialTick);
        Assert.Equal(manifest.StockAtCut,reply.Basis.StockAtCut);Assert.Equal(manifest.SlotId,reply.Basis.SlotId);Assert.Equal(manifest.AcquisitionReservationId,reply.Basis.AcquisitionReservationId);
        Assert.Equal(manifest.RuntimeInstanceId,reply.Assignment.RuntimeInstanceId);Assert.Equal(manifest.Attacker.SessionDigest,reply.Basis.Attacker.SessionDigest);
        var completed=reply.Basis.Clone();completed.InitialSnapshot=manifest.InitialSnapshot.Clone();completed.InitialTick=manifest.InitialTick;completed.Entities.Add(manifest.Entities.Select(e=>e.Clone()));
        Assert.NotNull(await service.ProposeAsync(BattlePreparedAdmissionTests.Request(executor,completed),default));
        Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
    }
    [Theory]
    [InlineData("scope")][InlineData("skip")][InlineData("offline")][InlineData("recovery")]
    public async Task Wrong_scope_skipped_proposal_offline_attacker_or_delivery_only_lease_cannot_get_a_preparation_basis(string fault)
    {
        using var f=await GuildFixture.Create(true);var(service,executor,manifest)=await BattlePreparedAdmissionTests.Prepare(f);var request=Request(executor,manifest);
        if(fault=="scope")request.Scope.Fence++;
        if(fault=="skip")request.ProposalSequence=2;
        if(fault=="offline")f.Sessions.End(f.Sessions.ByPlayer(2)!.Token);
        if(fault=="recovery")
        {
            var binding=f.Executors.BindingOf(executor)!;binding.Entry=null;binding.Lane=BattleExecutorLane.RecoveryDelivery;binding.BattleId=manifest.BattleId;
            binding.Attempt=manifest.Attempt;binding.Fence=manifest.Fence;binding.AssignmentTransitionId=manifest.PreparationTransitionId;
            request.Executor=f.Executors.Wire(f.Executors.RegisterRecoveryAuthenticated(binding,f.ExecutorBoot));
        }
        var error=await Record.ExceptionAsync(()=>service.ReadBasisAsync(request,default));Assert.True(error is InvalidDataException or OutpostEvidenceUnavailableException,error?.ToString()??"basis accepted");
    }
    private static BattlePreparationBasisRequest Request(BattleExecutorLease executor,BattlePreparedManifestV1 manifest)=>new(){Executor=new(){ExecutorId=executor.ExecutorId,
        LeaseId=executor.LeaseId,Fence=executor.Fence,ProfileDigest=executor.ProfileDigest},Scope=new(){WorldInstanceId=manifest.WorldInstanceId,RestoreBoundary=manifest.RestoreBoundary,
        BattleId=manifest.BattleId,Attempt=manifest.Attempt,Fence=manifest.Fence},ProposalSequence=1};
}
