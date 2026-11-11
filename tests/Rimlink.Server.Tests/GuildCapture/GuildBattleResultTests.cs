using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildBattleResultTests
{
    [Fact]
    public async Task A_commander_reads_the_same_committed_result_but_cannot_see_an_unapplied_final()
    {
        using var f=await GuildFixture.Create(true);var(executor,envelope,start)=await Prepare(f);
        var transport=BattleRuntimeStartedTests.Transport(f);var attacker=f.Sessions.ByPlayer(2)!;
        var request=new BattleCompletedViewRequest{Scope=new(){WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,
            BattleId=start.BattleId,Attempt=start.Attempt,Fence=start.Fence},
            StartDigest=ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray()))};
        Assert.Null((await transport.ReadCompletedViewAsync(attacker,request,default)).Receipt);
        var applied=await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        var first=await transport.ReadCompletedViewAsync(attacker,request,default);
        Assert.Equal(applied.Receipt,first.Receipt);
        Assert.Equal(BattleTerminalOutcome.AttackerWin,BattleManifestContracts.ReadCompletedView(first,start)!.Outcome);
        Assert.Equal(first,await transport.ReadCompletedViewAsync(attacker,request,default));

        var bad=request.Clone();bad.Scope.Fence++;
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(()=>transport.ReadCompletedViewAsync(attacker,bad,default));
        var tampered=first.Clone();tampered.Receipt.CurrentOwnerRevision++;
        Assert.Throws<InvalidDataException>(()=>BattleManifestContracts.ReadCompletedView(tampered,start));
    }

    [Fact]
    public async Task Trusted_capture_transfers_only_future_production_and_the_entire_old_stock_remains_an_obligation_once()
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,start) = await Prepare(f);
        var first = await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        Assert.Equal(first,await f.Service.ApplyBattleFinalAsync(executor,envelope,default));
        var row = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal(60,row.AllianceId); Assert.Equal(2,row.Revision); Assert.Equal(0,row.Stock); Assert.Equal(0,row.Remainder);
        var old = Assert.Single(await f.H.Db.Set<GuildResourceObligation>().AsNoTracking().ToArrayAsync());
        Assert.Equal(50,old.AllianceId); Assert.Equal(start.StockAtCut,old.OriginalCount); Assert.Equal(old.OriginalCount,old.RemainingCount);
        var battle = await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync();
        Assert.True(battle.Active); Assert.Equal("AppliedAwaitingCleanup",battle.Phase);
        Assert.False((await f.H.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync()).Active);
        Assert.False((await f.H.Db.Set<GuildBattleSlot>().AsNoTracking().SingleAsync()).Active);
        Assert.False((await f.H.Db.Set<GuildAcquisitionReservation>().AsNoTracking().SingleAsync()).Active);
        Assert.All(await f.H.Db.Set<GuildBattleControlGrant>().AsNoTracking().ToArrayAsync(),g => Assert.False(g.Active));
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(10,(await f.Service.SettleAsync(f.Scope,Guid.NewGuid().ToString("D"),row.Id,default)).Stock);
        Assert.Equal(start.StockAtCut,(await f.H.Db.Set<GuildResourceObligation>().AsNoTracking().SingleAsync()).RemainingCount);
        var replacement = f.RegisterRecovery(start);
        Assert.Equal(first,await f.Service.ApplyBattleFinalAsync(replacement,envelope,default));
        Assert.Single(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());
        Assert.Null(f.Executors.Available());
        var oldClaim = await f.Service.ClaimResourcesAsync(1,new(f.Scope,Guid.NewGuid().ToString("D"),50,"Obligation",old.Id,1,1,0,0,start.StockAtCut),default);
        var newClaim = await f.Service.ClaimResourcesAsync(2,new(f.Scope,Guid.NewGuid().ToString("D"),60,"Outpost",row.Id,2,2,0,0,10),default);
        Assert.NotEqual(oldClaim.ClaimId,newClaim.ClaimId);
        Assert.Equal(0,(await f.H.Db.Set<GuildResourceObligation>().AsNoTracking().SingleAsync()).RemainingCount);
        Assert.Equal(0,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Stock);
        var parcels = await f.H.Db.Parcels.AsNoTracking().Where(p => p.Id == oldClaim.ParcelId || p.Id == newClaim.ParcelId).ToArrayAsync();
        Assert.Equal(2,parcels.Length);
        Assert.All(parcels,p => { Assert.Equal(ParcelKindDb.GuildResources,p.Kind); Assert.Empty(p.Items); Assert.Empty(p.Pawns); Assert.Equal(0,p.Silver); });
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Theory]
    [InlineData(BattleTerminalOutcome.DefenderWin)]
    [InlineData(BattleTerminalOutcome.Draw)]
    [InlineData(BattleTerminalOutcome.TechnicalAbort)]
    public async Task A_non_capture_preserves_owner_stock_fraction_and_never_creates_an_extra_copy(BattleTerminalOutcome outcome)
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,start) = await Prepare(f,outcome);
        var oldShield = (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).ProtectedUntilUtc;
        var reply = await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        var row = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal(50,row.AllianceId); Assert.Equal(1,row.Revision); Assert.Equal(start.StockAtCut,row.Stock); Assert.Equal(start.RemainderAtCut,row.Remainder);
        Assert.Empty(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());
        Assert.Equal(outcome,reply.Receipt.Outcome);
        Assert.Equal(outcome == BattleTerminalOutcome.TechnicalAbort ? 1 : 0,row.ConsecutiveAborts);
        if (outcome == BattleTerminalOutcome.TechnicalAbort) Assert.Equal(oldShield,row.ProtectedUntilUtc);
        else Assert.Equal(TimeSpan.FromHours(72),row.ProtectedUntilUtc - new DateTime(reply.Receipt.TerminalUtcTicks,DateTimeKind.Utc));
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Theory]
    [InlineData("BeforePending",false)][InlineData("AfterPending",false)][InlineData("AfterDomainCommit",true)][InlineData("AfterExternalCommit",true)]
    public async Task Final_crash_and_exact_resend_award_old_stock_and_capture_at_most_once(string point,bool committed)
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,start) = await Prepare(f);
        f.H.Authority.CommitCheckpoint = checkpoint => { if (checkpoint == point) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => f.Service.ApplyBattleFinalAsync(executor,envelope,default));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed ? 60 : 50,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).AllianceId);
        var replay = await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        Assert.Equal(replay,await f.Service.ApplyBattleFinalAsync(executor,envelope,default));
        Assert.Single(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());
        Assert.Equal(2,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Revision);
    }
    [Fact]
    public async Task A_post_start_personal_freeze_does_not_annul_the_native_outcome()
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,_) = await Prepare(f);
        var player = await f.H.Db.Players.SingleAsync(p => p.Id == 2); player.EconomyFrozenAt = f.Time.Now.UtcDateTime;
        await f.H.Db.SaveChangesAsync(); f.H.Db.ChangeTracker.Clear();
        Assert.Equal(60,(await f.Service.ApplyBattleFinalAsync(executor,envelope,default)).Receipt.CurrentAllianceId);
    }
    [Theory]
    [InlineData("prepared")][InlineData("boundary")][InlineData("fence")][InlineData("missing")][InlineData("view")][InlineData("lease")]
    public async Task Missing_or_foreign_evidence_never_awards_a_result(string damage)
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,start) = await Prepare(f,startCommitted:damage != "prepared");
        var final = BattleFinalManifestV1.Parser.ParseFrom(envelope.Manifest);
        if (damage == "boundary") final.RestoreBoundary = Guid.NewGuid().ToString("D");
        if (damage == "fence") final.Fence++;
        if (damage == "lease") executor = executor with { LeaseId = Guid.NewGuid().ToString("D") };
        if (damage == "missing") {
            var descriptor = f.Artifacts.DescriptorHeld(start.BattleId,start.Fence,"final")!;
            File.Move(Path.Combine(f.H.Data.Archive,f.Artifacts.ValidateCompleteHeld(descriptor).RelativePath),Path.Combine(f.H.Data.Archive,"kept-unavailable-final"));
        }
        if (damage == "view") final.TerminalViewSequence++;
        envelope.Manifest = final.ToByteString();
        var error = await Record.ExceptionAsync(() => f.Service.ApplyBattleFinalAsync(executor,envelope,default));
        Assert.True(error is InvalidDataException or OutpostEvidenceUnavailableException,error?.ToString() ?? "invalid final unexpectedly applied");
        Assert.Equal(50,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).AllianceId);
        Assert.Empty(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());
        Assert.Null((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).FinalDigest);
    }
    [Fact]
    public async Task Profile_revocation_after_Start_does_not_discard_the_exact_durable_outcome()
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,start) = await Prepare(f);
        await f.Service.RevokeBattleProfileAsync(Rimlink.Server.Application.Admin.Actor.Api,f.Scope,Guid.NewGuid().ToString("D"),start.ApprovalTransitionId,"withdrawn fixture",default);
        Assert.Equal(60,(await f.Service.ApplyBattleFinalAsync(executor,envelope,default)).Receipt.CurrentAllianceId);
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Fact]
    public async Task Terminal_seal_closes_pending_commands_without_claiming_native_execution()
    {
        using var f = await GuildFixture.Create(true); var (executor,envelope,start) = await Prepare(f);
        BattleJournalScope scope = new(start.BattleId,start.WorldInstanceId,start.RestoreBoundary,start.ExecutorId,start.Fence,
            BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray()));
        using (await f.H.Files.PerStoreLock.HoldAsync(default))
        using (await f.Battles.HoldAsync(default))
            BattleCommandJournal.OpenHeld(f.Artifacts.CommandDirectoryHeld(start.BattleId,start.Fence),scope).Append(1,2,Guid.NewGuid().ToString("D"),"fixture-move"u8);
        await f.Service.ApplyBattleFinalAsync(executor,envelope,default);
        var journal = BattleCommandJournal.OpenHeld(f.Artifacts.CommandDirectoryHeld(start.BattleId,start.Fence),scope,createIfMissing:false);
        var pending = journal.Read(1,1); Assert.Null(pending.AcceptedTick);
        var acknowledgement = BattleCommandReceipt.Parser.ParseFrom(pending.Acknowledgement!);
        Assert.False(acknowledgement.HasAcceptedTick); Assert.Equal(BattleCommandDisposition.NotExecuted,acknowledgement.Disposition);
        Assert.Equal(BattleCommandReason.Terminal,acknowledgement.Reason); Assert.Equal(0,journal.Pending(1));
        journal.ValidateSeal();
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Final_waits_for_real_dispatched_outcome_and_cut_proof_without_inventing_an_ack(bool revoke)
    {
        using var f=await GuildFixture.Create(true);var(executor,envelope,start)=await Prepare(f);
        var scope=new BattleJournalScope(start.BattleId,start.WorldInstanceId,start.RestoreBoundary,start.ExecutorId,start.Fence,
            BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray()));
        byte[] command=new BattleCommandV1{Version=1,FighterId=start.Entities[0].FighterId,Move=new(){X=25,Z=25}}.ToByteArray();
        string? cut=null;
        using(await f.H.Files.PerStoreLock.HoldAsync(default))using(await f.Battles.HoldAsync(default))
        {
            var journal=BattleCommandJournal.OpenHeld(f.Artifacts.CommandDirectoryHeld(start.BattleId,start.Fence),scope);
            journal.AppendControlled(1,2,Guid.NewGuid().ToString("D"),command,new(start.Attacker.ControlGrantId,start.Attacker.ControlEpoch,start.Attacker.SessionDigest.ToByteArray(),1));
            journal.Dispatch(Guid.NewGuid().ToString("D"),[new(1,1)]);
            if(revoke)cut=journal.BeginCut(1,1,Guid.NewGuid().ToString("D")).Id;
        }
        const string credential="test-dedicated-executor-credential";
        using var server=new ServerFixture().WithSetting("GuildCapture:Enabled","true")
            .WithSetting("GuildCapture:ExecutorId",executor.ExecutorId)
            .WithSetting("GuildCapture:ExecutorTokenSha256",Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential))))
            .WithSetting("GuildCapture:ApprovedProfileDigest",Convert.ToHexStringLower(executor.ProfileDigest.Span))
            .WithServices(services=>services.AddScoped<BattleExecutorGrpcService>(_=>new(f.Executors,f.H.Files,null!,null!,f.Service,null!,null!,null!)));
        using var channel=server.CreateChannel();
        var api=new BattleExecutor.BattleExecutorClient(channel);
        var request=new BattleFinalRequest{Executor=new(){ExecutorId=executor.ExecutorId,LeaseId=executor.LeaseId,Fence=executor.Fence,
            ProfileDigest=executor.ProfileDigest},Envelope=envelope};
        var headers=new Metadata{{"authorization","RimlinkExecutor "+credential}};
        int pendingWrites=0;f.H.Authority.CommitCheckpoint=point=>{if(point=="BeforePending")pendingWrites++;};
        var error=await Assert.ThrowsAsync<RpcException>(()=>api.FinalAsync(request,headers).ResponseAsync);
        Assert.Equal(0,pendingWrites);f.H.Authority.CommitCheckpoint=null;
        Assert.Equal(StatusCode.Unavailable,error.StatusCode);
        Assert.Equal(ErrorCode.GuildUnavailable,error.ErrorDetail()!.Code);
        Assert.Equal(50,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).AllianceId);
        Assert.Empty(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());
        using(await f.H.Files.PerStoreLock.HoldAsync(default))using(await f.Battles.HoldAsync(default))
        {
            var journal=BattleCommandJournal.OpenHeld(f.Artifacts.CommandDirectoryHeld(start.BattleId,start.Fence),scope,false);
            Assert.Null(journal.Read(1,1).Acknowledgement);
            var executed=new BattleCommandReceipt{Side=BattleFighterSide.Attacker,Sequence=1,CommandDigest=ByteString.CopyFrom(SHA256.HashData(command)),
                AcceptedTick=10,Disposition=BattleCommandDisposition.Executed,Reason=BattleCommandReason.None};
            journal.Acknowledge(1,1,10,executed.ToByteArray());if(cut is not null)journal.AcknowledgeCut(cut);
        }
        var applied=await api.FinalAsync(request,headers).ResponseAsync;
        Assert.Equal(60,applied.Receipt.CurrentAllianceId);
        Assert.Equal(60,(await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).AllianceId);
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }

    private sealed class Interrupted : Exception;

    [Fact]
    public async Task A_committed_Start_without_a_durable_native_runtime_binding_cannot_award_capture()
    {
        using var f=await GuildFixture.Create(true);var(executor,envelope,_)=await Prepare(f,runtimeBound:false);
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Service.ApplyBattleFinalAsync(executor,envelope,default));
        var row=await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();Assert.Equal(50,row.AllianceId);Assert.Equal(1,row.Revision);
        Assert.Empty(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());
    }
    internal static async Task<(BattleExecutorLease Executor,BattleFinalEnvelope Envelope,BattlePreparedManifestV1 Start)> Prepare(GuildFixture f,
        BattleTerminalOutcome outcome = BattleTerminalOutcome.AttackerWin,bool startCommitted = true,bool runtimeBound=true)
    {
        var (request,start) = await GuildBattleStartTests.Prepare(f,true); if (startCommitted) await f.Service.StartBattleAsync(request,default);
        if(startCommitted && runtimeBound)await BattleRuntimeStartedTests.Transport(f).RuntimeStartedAsync(BattleRuntimeStartedTests.Request(request,start),default);
        var executor = BattleResultStaging.Lease(request.Executor);
        var final = new BattleFinalManifestV1 { Version = 1,WorldInstanceId = start.WorldInstanceId,RestoreBoundary = start.RestoreBoundary,
            CampaignId = start.CampaignId,BattleId = start.BattleId,Attempt = start.Attempt,Fence = start.Fence,ExecutorId = start.ExecutorId,
            RuntimeInstanceId = start.RuntimeInstanceId,OriginalExecutorLeaseId = start.ExecutorLeaseId,OriginalExecutorLeaseFence = start.ExecutorLeaseFence,
            StartNonce = start.StartNonce,StartDigest = ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray())),
            ProfileDigest = start.ProfileDigest,InitialSnapshot = start.InitialSnapshot.Clone(),TerminalTick = start.InitialTick + (outcome == BattleTerminalOutcome.AttackerWin ? 3000 : 1),
            HeldTicks = outcome == BattleTerminalOutcome.AttackerWin ? 3000 : 0,Outcome = outcome,Reason = outcome switch {
                BattleTerminalOutcome.AttackerWin => BattleTerminalReason.ZoneHeld,BattleTerminalOutcome.DefenderWin => BattleTerminalReason.SideEliminated,
                BattleTerminalOutcome.Draw => BattleTerminalReason.MutualElimination,_ => BattleTerminalReason.InvalidState },WallElapsedMs = 20000,TerminalViewSequence = 1 };
        BattleDocumentDescriptor Document(BattleDocumentKind kind,byte[] bytes) => new() { Kind = kind,SizeBytes = bytes.Length,Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)) };
        byte[] snapshot = "fixture-final-not-a-native-proof"u8.ToArray(), simulation = "fixture-simulation-not-a-native-proof"u8.ToArray();
        final.FinalSnapshot = Document(BattleDocumentKind.FinalSnapshot,snapshot); final.SimulationJournal = Document(BattleDocumentKind.SimulationJournal,simulation);
        foreach (var fighter in start.Entities) final.Pawns.Add(new BattleTerminalPawn { FighterId = fighter.FighterId,EntityId = fighter.EntityId,Side = fighter.Side,
            X = outcome == BattleTerminalOutcome.AttackerWin && fighter.Side == BattleFighterSide.Attacker ? 25 : 40,Z = 25,Alive = true,CanFight = true,Health = 1,
            Downed = outcome == BattleTerminalOutcome.Draw || outcome == BattleTerminalOutcome.DefenderWin && fighter.Side == BattleFighterSide.Attacker });
        var view = new BattleTerminalViewV1 { Version = 1,WorldInstanceId = final.WorldInstanceId,RestoreBoundary = final.RestoreBoundary,BattleId = final.BattleId,
            Attempt = final.Attempt,Fence = final.Fence,StartDigest = final.StartDigest,Sequence = final.TerminalViewSequence,TerminalTick = final.TerminalTick,
            HeldTicks = final.HeldTicks,Outcome = final.Outcome,Reason = final.Reason,Snapshot = final.FinalSnapshot.Clone() };
        view.Pawns.Add(final.Pawns.Select(p => p.Clone())); byte[] viewBytes = view.ToByteArray(); final.TerminalView = Document(BattleDocumentKind.TerminalView,viewBytes);
        using (await f.H.Files.PerStoreLock.HoldAsync(default))
        using (await f.Battles.HoldAsync(default))
            foreach (var (kind,document,bytes) in new[] { ("final",final.FinalSnapshot,snapshot),("simulation",final.SimulationJournal,simulation),("view",final.TerminalView,viewBytes) })
                f.Artifacts.AppendHeld(BattleResultStaging.Descriptor(final,kind,document),0,bytes);
        return (executor,new() { ClientRequestId = Guid.NewGuid().ToString("D"),Manifest = final.ToByteString() },start);
    }
}
