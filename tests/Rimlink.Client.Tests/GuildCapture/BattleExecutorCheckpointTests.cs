using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Net;
using Grpc.Core;
using Rimlink.Client.Net;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleExecutorCheckpointTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"rimlink-executor-checkpoint-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
    private BattleExecutorSettings Settings(BattlePreparedManifestV1 start,string endpoint="https://localhost:27555/")=>new(){StateDirectory=directory,Endpoint=endpoint,CertificateSha256=new string('a',64),
        ExecutorId=start.ExecutorId,WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,ProfileDigest=Convert.ToHexStringLower(start.ProfileDigest.Span),ProfileSha256=new string('b',64)};
    private static BattleRuntimeAssignmentV1 Assignment(BattlePreparedManifestV1 start)=>new(){Version=1,Scope=new(){WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,BattleId=start.BattleId,Attempt=start.Attempt,Fence=start.Fence},
        CampaignId=start.CampaignId,ExecutorId=start.ExecutorId,RuntimeInstanceId=start.RuntimeInstanceId,OriginalLeaseId=start.ExecutorLeaseId,OriginalLeaseFence=start.ExecutorLeaseFence,
        StartNonce=start.StartNonce,ApprovalTransitionId=start.ApprovalTransitionId,ProfileDigest=start.ProfileDigest,InventorySha256=start.MeasuredInventory.Sha256,
        ExecutorBuildSha256=ByteString.CopyFrom(SHA256.HashData("build"u8)),AssignedAtUtcTicks=start.ProductionCutUtcTicks,TransitionId=start.PreparationTransitionId};
    [Fact]
    public void Assignment_is_durable_before_native_work_and_a_cold_reader_never_gets_a_resume_permit()
    {
        var start=BattleManifestContractsTests.Manifest();var settings=Settings(start);var store=new BattleExecutorCheckpointStore(settings);
        var state=store.Create(Assignment(start));store.Save(state);var cold=new BattleExecutorCheckpointStore(settings);Assert.Equal(state,cold.Read());
        Assert.Throws<InvalidDataException>(()=>cold.ReadColdEnvelope());
        using var input=File.Open(store.Path,FileMode.Open,FileAccess.Read,FileShare.None);
    }
    [Fact]
    public void A_new_process_cannot_rewrite_original_assignment_endpoint_or_stage_to_claim_a_finished_match()
    {
        var start=BattleManifestContractsTests.Manifest();var store=new BattleExecutorCheckpointStore(Settings(start));var state=store.Create(Assignment(start));store.Save(state);
        var changed=state.Clone();changed.Assignment.RuntimeInstanceId=Guid.NewGuid().ToString("D");Assert.Throws<InvalidDataException>(()=>store.Save(changed));
        changed=state.Clone();changed.Stage=BattleExecutorCheckpointStage.Started;Assert.Throws<InvalidDataException>(()=>store.Save(changed));
        var wrong=Settings(start,"https://other.invalid/");Assert.Throws<InvalidDataException>(()=>new BattleExecutorCheckpointStore(wrong).Read());
        Assert.Equal(state,store.Read());
    }
    [Fact]
    public void A_cold_reader_returns_only_the_exact_complete_envelope_and_refuses_a_changed_artifact()
    {
        var(store,settings,capture,simulation)=CompleteState();var state=store.Read();
        var cold=new BattleExecutorCheckpointStore(settings);Assert.Equal(capture.Envelope,cold.ReadColdEnvelope());
        simulation[0]^=1;File.WriteAllBytes(store.ArtifactPath("simulation"),simulation);
        Assert.Throws<InvalidDataException>(()=>cold.ReadColdEnvelope());Assert.Equal(state,cold.Read());
    }
    private (BattleExecutorCheckpointStore Store,BattleExecutorSettings Settings,BattleFinalCapture Capture,byte[] Simulation) CompleteState()
    {
        var start=BattleManifestContractsTests.Manifest();byte[] initial="fixture initial, not native evidence"u8.ToArray(),final="fixture final"u8.ToArray(),simulation="fixture simulation"u8.ToArray();
        static BattleDocumentDescriptor Doc(BattleDocumentKind kind,byte[] bytes)=>new(){Kind=kind,SizeBytes=bytes.Length,Sha256=ByteString.CopyFrom(SHA256.HashData(bytes))};
        start.InitialSnapshot=Doc(BattleDocumentKind.InitialSnapshot,initial);
        var store=new BattleExecutorCheckpointStore(Settings(start));var state=store.Create(Assignment(start));store.Save(state);
        state.Stage=BattleExecutorCheckpointStage.Initial;store.Save(state);state.PreparedManifest=start.ToByteString();state.Stage=BattleExecutorCheckpointStage.Prepared;store.Save(state);
        state.Start=new(){Scope=state.Assignment.Scope.Clone(),Manifest=state.PreparedManifest,ManifestDigest=ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,state.PreparedManifest.Span)),
            StartedAtUnixMs=1,TransitionId=Guid.NewGuid().ToString("D"),ReadyTransitionId=Guid.NewGuid().ToString("D"),AuthorityHeadId=Guid.NewGuid().ToString("D"),AuthorityHeadDigest=ByteString.CopyFrom(new byte[32])};
        state.Stage=BattleExecutorCheckpointStage.Started;store.Save(state);
        var pawns=start.Entities.Select(e=>new BattlePawn(e.FighterId,(BattleSide)(int)e.Side,e.Side==BattleFighterSide.Attacker?25:40,25,true,false,false,true,1)).ToArray();
        var capture=BattleFinalCapture.Create(start,new(start.InitialTick+3000,3000,BattleOutcome.AttackerWin,BattleEndReason.ZoneHeld),pawns,TimeSpan.FromSeconds(1),TimeSpan.Zero,
            Doc(BattleDocumentKind.FinalSnapshot,final),Doc(BattleDocumentKind.SimulationJournal,simulation),7,Guid.NewGuid().ToString("D"));
        byte[] envelope=capture.Envelope.ToByteArray();Directory.CreateDirectory(Path.GetDirectoryName(store.ArtifactPath("initial"))!);
        foreach(var(kind,bytes)in new[]{("initial",initial),("final",final),("simulation",simulation),("view",capture.View.ToByteArray()),("envelope",envelope)})File.WriteAllBytes(store.ArtifactPath(kind),bytes);
        state.FinalEnvelopeSha256=ByteString.CopyFrom(SHA256.HashData(envelope));state.Stage=BattleExecutorCheckpointStage.FinalCaptured;store.Save(state);
        return(store,Settings(start),capture,simulation);
    }
    [Fact]
    public async Task Lost_Final_response_keeps_the_exact_envelope_and_retry_records_only_the_same_server_receipt()
    {
        var(store,settings,capture,_)=CompleteState();var state=store.Read()!;using var backend=new FinalWire(capture,state.Assignment);
        using var connection=new BattleExecutorConnection(settings,new string('s',32),new ConnectionTransport(new("http://localhost"),backend));await connection.Register(default);
        await Assert.ThrowsAsync<RpcException>(()=>BattleFinalDelivery.Deliver(store,connection,default));
        Assert.Equal(BattleExecutorCheckpointStage.FinalCaptured,store.Read()!.Stage);Assert.Null(store.Read()!.Applied);
        var applied=await BattleFinalDelivery.Deliver(store,connection,default);Assert.Equal(BattleExecutorCheckpointStage.Applied,store.Read()!.Stage);
        Assert.Equal(applied,await BattleFinalDelivery.Deliver(store,connection,default));Assert.Equal(applied.Receipt,store.Read()!.Applied);
        Assert.Equal(3,backend.FinalCalls);Assert.Equal(capture.Envelope,store.ReadColdEnvelope());
    }
    [Theory]
    [InlineData("progress")][InlineData("artifact")][InlineData("receipt")]
    public async Task A_wrong_artifact_or_Final_reply_cannot_mark_the_checkpoint_applied(string damage)
    {
        var(store,settings,capture,_)=CompleteState();var before=store.Read()!;using var backend=new FinalWire(capture,before.Assignment,damage);
        using var connection=new BattleExecutorConnection(settings,new string('s',32),new ConnectionTransport(new("http://localhost"),backend));await connection.Register(default);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BattleFinalDelivery.Deliver(store,connection,default));Assert.Equal(before,store.Read());
    }
    private sealed class FinalWire(BattleFinalCapture capture,BattleRuntimeAssignmentV1 assignment,string damage=""):HttpMessageHandler
    {
        internal int FinalCalls,InitialCalls;
        private readonly BattleExecutorLeaseMessage lease=new(){ExecutorId=assignment.ExecutorId,LeaseId=Guid.NewGuid().ToString("D"),Fence=1,ProfileDigest=assignment.ProfileDigest};
        private readonly BattleFinalReply final=new(){AuthorityHeadId=Guid.NewGuid().ToString("D"),AuthorityHeadDigest=ByteString.CopyFrom(new byte[32]),Receipt=new(){Version=1,
            WorldInstanceId=assignment.Scope.WorldInstanceId,RestoreBoundary=assignment.Scope.RestoreBoundary,BattleId=assignment.Scope.BattleId,CampaignId=assignment.CampaignId,
            Attempt=assignment.Scope.Attempt,Fence=assignment.Scope.Fence,ClientRequestId=capture.Envelope.ClientRequestId,TransitionId=Guid.NewGuid().ToString("D"),
            CommittedFinalDigest=ByteString.CopyFrom(SHA256.HashData("committed fixture"u8)),Outcome=capture.Final.Outcome,Reason=capture.Final.Reason}};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("RimlinkExecutor "+new string('s',32),Assert.Single(request.Headers.GetValues("authorization")));
            IMessage reply;bool lost=false;string method=request.RequestUri!.AbsolutePath;
            byte[] body=await request.Content!.ReadAsByteArrayAsync(ct);Assert.Equal(body.Length-5,BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(1,4)));
            if(method.EndsWith("/UploadInitial",StringComparison.Ordinal))
            {
                var chunk=BattleArtifactChunk.Parser.ParseFrom(body[5..]);InitialCalls++;Assert.Equal(assignment.Scope,chunk.Scope);
                reply=new BattleArtifactProgressMessage{ReceivedBytes=chunk.TotalBytes,Complete=true,Artifact=new(){Scope=chunk.Scope.Clone(),Kind="initial",Sha256=chunk.Sha256,SizeBytes=chunk.TotalBytes}};
            }
            else if(method.EndsWith("/UploadResultArtifact",StringComparison.Ordinal))
            {
                var chunk=BattleResultArtifactChunk.Parser.ParseFrom(body[5..]);Assert.Equal(assignment.Scope,chunk.Chunk.Scope);
                var progress=new BattleArtifactProgressMessage{ReceivedBytes=chunk.Chunk.TotalBytes,Complete=true,Artifact=new(){Scope=chunk.Chunk.Scope.Clone(),Kind=chunk.Kind,Sha256=chunk.Chunk.Sha256,SizeBytes=chunk.Chunk.TotalBytes}};
                if(damage=="progress")progress.ReceivedBytes++;if(damage=="artifact")progress.Artifact.Scope.BattleId=Guid.NewGuid().ToString("D");reply=progress;
            }
            else if(method.EndsWith("/Final",StringComparison.Ordinal))
            {Assert.Equal(capture.Envelope,BattleFinalRequest.Parser.ParseFrom(body[5..]).Envelope);FinalCalls++;lost=FinalCalls==1 && damage=="";var result=final.Clone();if(damage=="receipt")result.Receipt.ClientRequestId=Guid.NewGuid().ToString("D");reply=result;}
            else reply=lease;
            byte[] bytes=reply.ToByteArray(),frame=new byte[bytes.Length+5];BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1,4),bytes.Length);bytes.CopyTo(frame,5);
            var response=new HttpResponseMessage(HttpStatusCode.OK){Version=new(2,0),Content=new ByteArrayContent(lost?Array.Empty<byte>():frame)};
            response.Content.Headers.ContentType=new("application/grpc");response.TrailingHeaders.Add("grpc-status",lost?"14":"0");return response;
        }
    }
    [Fact]
    public void Cancelled_before_Start_keeps_its_terminal_reference_and_never_invents_a_Final_envelope()
    {
        var start=BattleManifestContractsTests.Manifest();var store=new BattleExecutorCheckpointStore(Settings(start));var state=store.Create(Assignment(start));store.Save(state);
        state.CancellationTransitionId=Guid.NewGuid().ToString("D");state.Stage=BattleExecutorCheckpointStage.Cancelled;store.Save(state);
        state.Stage=BattleExecutorCheckpointStage.AtEntry;store.Save(state);
        state.Cleanup=new(){Version=1,Scope=state.Assignment.Scope.Clone(),TransitionId=Guid.NewGuid().ToString("D"),RuntimeInstanceId=start.RuntimeInstanceId,
            CleanedAtUtcTicks=DateTime.UtcNow.Ticks,ProofKind=BattleCleanupProofKind.SameProcessEntry,Terminal=new(){Version=1,Kind=BattleCleanupTerminalKind.PreStartCancellation,
                TerminalTransitionId=state.CancellationTransitionId,AssignmentTransitionId=state.Assignment.TransitionId}};
        state.Stage=BattleExecutorCheckpointStage.Cleaned;store.Save(state);Assert.Equal(state,store.Read());Assert.Null(store.Read()!.Start);Assert.True(store.Read()!.FinalEnvelopeSha256.IsEmpty);
        Assert.Throws<InvalidDataException>(()=>store.ReadColdEnvelope());
        string archived=store.ArchiveCleaned();Assert.Null(store.Read());Assert.Equal(state.ToByteArray(),File.ReadAllBytes(archived));
        Assert.Equal(state, store.ReadCompleted(state.Assignment.Scope));
        Assert.Throws<InvalidDataException>(() => store.Create(state.Assignment));
        var wrongScope = state.Assignment.Scope.Clone();
        wrongScope.RestoreBoundary = Guid.NewGuid().ToString("D");
        Assert.Throws<InvalidDataException>(() => store.ReadCompleted(wrongScope));
        var next=Assignment(start);next.Scope.BattleId=Guid.NewGuid().ToString("D");next.TransitionId=Guid.NewGuid().ToString("D");store.Save(store.Create(next));
        Assert.Equal(next,store.Read()!.Assignment);Assert.Equal(state.ToByteArray(),File.ReadAllBytes(archived));
    }
    [Fact]
    public void Started_abort_keeps_the_exact_Start_and_prevents_delivery_of_an_already_captured_Final()
    {
        var (store, _, _, _) = CompleteState();
        var state = store.Read()!;
        var final = state.FinalEnvelopeSha256;
        state.CancellationTransitionId = Guid.NewGuid().ToString("D");
        state.AbortTerminal = new() { Version = 1, Kind = BattleCleanupTerminalKind.StartedAbort,
            TerminalTransitionId = state.CancellationTransitionId, AssignmentTransitionId = state.Assignment.TransitionId,
            AbortedStartDigest = ByteString.CopyFrom(new byte[32]) };
        state.Stage = BattleExecutorCheckpointStage.Cancelled;
        Assert.Throws<InvalidDataException>(() => store.Save(state));
        state.AbortTerminal.AbortedStartDigest = state.Start.ManifestDigest;
        store.Save(state);
        Assert.Equal(final, store.Read()!.FinalEnvelopeSha256);
        Assert.Throws<InvalidDataException>(() => store.ReadColdEnvelope());
        Assert.Throws<InvalidDataException>(() => store.ArchiveCleaned());

        state.Stage = BattleExecutorCheckpointStage.AtEntry;
        store.Save(state);
        state.Cleanup = new() { Version = 1, Scope = state.Assignment.Scope.Clone(), TransitionId = Guid.NewGuid().ToString("D"),
            RuntimeInstanceId = state.Assignment.RuntimeInstanceId, CleanedAtUtcTicks = DateTime.UtcNow.Ticks,
            ProofKind = BattleCleanupProofKind.SameProcessEntry, Terminal = state.AbortTerminal.Clone() };
        state.Stage = BattleExecutorCheckpointStage.Cleaned;
        store.Save(state);
        store.ArchiveCleaned();
        Assert.Equal(state, store.ReadCompleted(state.Assignment.Scope));
    }

    [Fact]
    public void A_live_or_cancelled_assignment_cannot_be_archived_to_erase_its_occupancy()
    {
        var start=BattleManifestContractsTests.Manifest();var store=new BattleExecutorCheckpointStore(Settings(start));var state=store.Create(Assignment(start));store.Save(state);
        Assert.Throws<InvalidDataException>(()=>store.ArchiveCleaned());
        state.CancellationTransitionId=Guid.NewGuid().ToString("D");state.Stage=BattleExecutorCheckpointStage.Cancelled;store.Save(state);
        Assert.Throws<InvalidDataException>(()=>store.ArchiveCleaned());Assert.Equal(state,store.Read());
    }
    [Theory]
    [InlineData("torn")][InlineData("unknown")][InlineData("part")]
    public void Unknown_or_partial_checkpoint_evidence_is_preserved(string damage)
    {
        var start=BattleManifestContractsTests.Manifest();var store=new BattleExecutorCheckpointStore(Settings(start));store.Save(store.Create(Assignment(start)));
        byte[] bytes=File.ReadAllBytes(store.Path);if(damage=="torn")bytes=bytes[..^1];if(damage=="unknown")bytes=[..bytes,0xA0,0x06,0x01];File.WriteAllBytes(store.Path,bytes);
        if(damage=="part")File.WriteAllText(store.Path+".tmp","incomplete");
        Assert.Throws<InvalidDataException>(()=>store.Read());Assert.Equal(bytes,File.ReadAllBytes(store.Path));
    }
    [Fact]
    public void A_different_configured_restore_boundary_cannot_adopt_or_erase_a_checkpoint()
    {
        var start=BattleManifestContractsTests.Manifest();var store=new BattleExecutorCheckpointStore(Settings(start));store.Save(store.Create(Assignment(start)));byte[] before=File.ReadAllBytes(store.Path);
        start.RestoreBoundary=Guid.NewGuid().ToString("D");var foreign=new BattleExecutorCheckpointStore(Settings(start));
        Assert.Throws<InvalidDataException>(()=>foreign.Read());Assert.Equal(before,File.ReadAllBytes(store.Path));
    }
    [Fact]
    public async Task Initial_upload_uses_its_own_RPC_and_checks_the_frozen_raw_file_before_sending()
    {
        var(store,settings,capture,_)=CompleteState();var assignment=store.Read()!.Assignment;var initial=BattleManifestContracts.ReadPrepared(store.Read()!.Start.Manifest.Span).InitialSnapshot;
        using var backend=new FinalWire(capture,assignment);using var connection=new BattleExecutorConnection(settings,new string('s',32),new ConnectionTransport(new("http://localhost"),backend));await connection.Register(default);
        await BattleArtifactDelivery.Upload(connection,assignment.Scope,"initial",initial,store.ArtifactPath("initial"),default);Assert.Equal(1,backend.InitialCalls);Assert.Equal(0,backend.FinalCalls);
        File.WriteAllText(store.ArtifactPath("initial"),"corrupt initial");
        await Assert.ThrowsAsync<InvalidDataException>(()=>BattleArtifactDelivery.Upload(connection,assignment.Scope,"initial",initial,store.ArtifactPath("initial"),default));Assert.Equal(1,backend.InitialCalls);
    }
}
