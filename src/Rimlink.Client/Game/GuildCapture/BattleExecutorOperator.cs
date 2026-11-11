using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

// This process is selected explicitly before any player login or settings redirection.
internal sealed partial class BattleExecutorOperator(BattleExecutorSettings settings)
{
    private readonly string runtimeId=Guid.NewGuid().ToString("D");
    private readonly SemaphoreSlim reconnect=new(1,1);
    private BattleExecutorProfile profile=null!;
    private BattleExecutorCheckpointStore store=null!;
    private BattleExecutorCheckpointV1? state;
    private BattleExecutorConnection connection=null!;
    private FileStream ownership=null!;
    private BattleScenario? scenario;
    private BattleTickController? controller;
    private BattleExecutorRunningPump? pump;
    private int callbacks;
    private CancellationTokenSource heartbeatLife=new();
    private Task heartbeat=Task.CompletedTask;
    internal string Status="Loading";

    internal async Task Run()
    {
        string saveData=await MainThread.Get(()=>GenFilePaths.SaveDataFolderPath);
        var explicitData=Environment.GetCommandLineArgs().Where(a=>a.StartsWith("-savedatafolder=",StringComparison.OrdinalIgnoreCase)).ToArray();
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        if(explicitData.Length!=1 || !Path.IsPathFullyQualified(explicitData[0][16..]) || !Path.GetFullPath(explicitData[0][16..]).Equals(Path.GetFullPath(saveData),comparison)
            || !Path.GetFullPath(settings.StateDirectory).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(saveData))+Path.DirectorySeparatorChar,comparison))throw Invalid();
        NoLinks(settings.StateDirectory);Directory.CreateDirectory(settings.StateDirectory);
        string ownerPath=Path.Combine(settings.StateDirectory,"executor.owner");NoLinks(ownerPath);
        ownership=new(ownerPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        profile=BattleExecutorProfile.Load(settings);await VerifyRuntime();store=new(settings);state=store.Read();
        if(state?.Stage==BattleExecutorCheckpointStage.Cleaned){store.ArchiveCleaned();state=null;}
        if (File.Exists(settings.RestoreCleanupPath()) && settings.ReadRestoreCleanup().ReplacementAssignmentId is not null)
        {
            if (state is not null) throw Invalid();
            await RestoreCleanup();
            return;
        }
        connection=new(settings,settings.ReadCredential());
        if(state is not null)
        {
            // A file can deliver a frozen result, but cannot establish the original process or live Game context.
            if(state.Start is null || state.FinalEnvelopeSha256.IsEmpty || state.AbortTerminal is not null)throw Invalid();
            await Register(BattleExecutorLane.RecoveryDelivery);Status="Delivering";
            await Retry(()=>BattleFinalDelivery.Deliver(store,connection,default),refused:true);state=store.Read();Status="RecoveryRequired";return;
        }
        await Register(BattleExecutorLane.Execution);StartHeartbeat();
        try
        {
            while(true)
            {
                if (File.Exists(settings.RestoreCleanupPath())) break;
                Status="Idle";var work=await Retry(()=>connection.Poll(default),refused:true);
                if (File.Exists(settings.RestoreCleanupPath())) break;
                if(work.Preparing is not null || work.Cancelled is not null)
                {
                    var assignment=BattleRuntimeAssignmentContracts.Read((work.Preparing?.Assignment??work.Cancelled!.Assignment).ToByteArray());
                    if(assignment.RuntimeInstanceId!=runtimeId || assignment.ExecutorId!=settings.ExecutorId || assignment.Scope.WorldInstanceId!=settings.WorldInstanceId
                        || assignment.Scope.RestoreBoundary!=settings.RestoreBoundary || !assignment.ProfileDigest.Equals(profile.Digest))throw Invalid();
                    state=store.Create(assignment);store.Save(state);
                    await Native(()=>{if(Current.ProgramState!=ProgramState.Entry || Current.Game is not null)throw Invalid();BattleGameContext.BeginExecutor(assignment.Scope);return true;});
                    if(work.Cancelled is {} cancelled)await Cancel(cancelled);
                    else await Prepare(work.Preparing!);
                    if(state?.Stage is BattleExecutorCheckpointStage.Applied or BattleExecutorCheckpointStage.Cancelled)await Cleanup();
                    if(state is not null)throw Invalid();
                }
                await Task.Delay(1000);
            }
        }
        catch (RestoreCleanupRequested) { }
        finally{await StopHeartbeat();GC.KeepAlive(ownership);}
        await RestoreCleanup();
    }
    private async Task Prepare(BattlePreparationWork work)
    {
        Status="Preparing";
        if(!work.Scope.Equals(state!.Assignment.Scope) || work.CampaignId!=state.Assignment.CampaignId || !work.StartNonce.Equals(state.Assignment.StartNonce))throw Invalid();
        byte[] template=profile.ReadTemplate();
        scenario=await Native(()=>{var arena=new BattleScenario(profile.Profile,work.Scope,settings.StateDirectory);arena.LoadTemplate(template);return arena;});
        await Loaded();await Native(()=>{scenario.CreateArenaAndReload();return true;});await Loaded();
        await Native(()=>{scenario.AttachReloadedInitial();return true;});SaveState(s=>s.Stage=BattleExecutorCheckpointStage.Initial);
        await Retry(async()=>{await BattleArtifactDelivery.Upload(connection,work.Scope,"initial",scenario.InitialSnapshot!,scenario.ArtifactPath("initial"),default);return true;});
        int sequence=1;
        while(true)
        {
            Status="WaitingReady";
            if(!state!.PreparedManifest.IsEmpty)
            {
                var request = StartRequest(state.PreparedManifest);
                BattleStartAuthorization? authorization = null;
                try
                {
                    authorization = await connection.Api.StartAsync(request, connection.Options(default));
                }
                catch(RpcException error)when(error.StatusCode is StatusCode.FailedPrecondition or StatusCode.ResourceExhausted)
                {await EnsureTransport();}
                catch(RpcException error)when(error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
                {await RecoverTransport();continue;}
                if (authorization is not null)
                {
                    SaveState(s => { s.Start = authorization.Clone(); s.Stage = BattleExecutorCheckpointStage.Started; });
                    await RunBattle(request.ClientRequestId, authorization);
                    return;
                }
            }
            try
            {
                var polled=await connection.Poll(default);if(polled.Cancelled is {} cancelled){await Cancel(cancelled);return;}
                sequence=state.PreparedManifest.IsEmpty?sequence:BattleManifestContracts.ReadPrepared(state.PreparedManifest.Span).ProposalSequence;
                var basis=await connection.Api.ReadPreparationAsync(new(){Executor=connection.Lease,Scope=work.Scope.Clone(),ProposalSequence=sequence},connection.Options(default));
                var measured=await Native(()=>{scenario.VerifyInitial();return(Find.TickManager.TicksGame,Entities());});
                var candidate=BattlePreparationBuilder.Build(state.Assignment,profile.Profile,basis,scenario.InitialSnapshot!,measured.Item1,measured.Item2);
                if(!state.PreparedManifest.IsEmpty && !candidate.ToByteString().Equals(state.PreparedManifest))
                {
                    if(++sequence>8)throw Invalid();
                    basis=await connection.Api.ReadPreparationAsync(new(){Executor=connection.Lease,Scope=work.Scope.Clone(),ProposalSequence=sequence},connection.Options(default));
                    candidate=BattlePreparationBuilder.Build(state.Assignment,profile.Profile,basis,scenario.InitialSnapshot!,measured.Item1,measured.Item2);
                }
                if(!candidate.ToByteString().Equals(state.PreparedManifest))SaveState(s=>{s.PreparedManifest=candidate.ToByteString();s.Stage=BattleExecutorCheckpointStage.Prepared;});
                var proposed=await connection.Api.ProposePreparedAsync(new(){Executor=connection.Lease,Manifest=state.PreparedManifest},connection.Options(default));
                if(proposed.ProposalSequence!=sequence || !proposed.ManifestDigest.Span.SequenceEqual(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,state.PreparedManifest.Span)))throw Invalid();
            }
            catch(RpcException error)when(error.StatusCode is StatusCode.FailedPrecondition or StatusCode.ResourceExhausted)
            {await EnsureTransport();}
            catch(RpcException error)when(error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
            {await RecoverTransport();}
            await Task.Delay(2000);
        }
    }
    private async Task RunBattle(string startRequestId,BattleStartAuthorization authorization)
    {
        var terminal=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller=await Native(()=>new BattleTickController(scenario!,authorization,runtimeId,settings,profile,(result,pawns,wall,lost,bytes)=>
        {
            try
            {
                var start=BattleManifestContracts.ReadPrepared(authorization.Manifest.Span);
                var frozen=BattleArtifactCapture.Capture(scenario!,start,result,pawns,wall,lost,bytes,(long)result.Tick-start.InitialTick+2,Guid.NewGuid().ToString("D"));
                SaveState(s=>{s.FinalEnvelopeSha256=ByteString.CopyFrom(SHA256.HashData(frozen.Envelope.ToByteArray()));s.Stage=BattleExecutorCheckpointStage.FinalCaptured;});terminal.TrySetResult(true);
            }
            catch(Exception error){terminal.TrySetException(error);throw;}
        }));
        var proof=await Native(()=>controller.RuntimeProof(startRequestId));long sentAt=Stopwatch.GetTimestamp();
        BattleRuntimeStartedReply ready;
        try { ready = await Retry(() => connection.RuntimeStarted(proof, default)); }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            var abort = await Retry(() => BattleExecutorRunningPump.ReadAbortAsync(connection, default, tolerateTransient: false));
            if (abort is null) throw;
            await Cancel(abort); return;
        }
        await Native(()=>{controller.Activate(ready,sentAt);return true;});
        Status="Running";using var running=new CancellationTokenSource();pump=new(()=>connection,controller,state!.Assignment.Scope,RecoverTransport,()=>state?.Applied is not null);
        var pumping=pump.Run(running.Token);
        try
        {
            if (await Task.WhenAny(terminal.Task, pumping) == pumping)
            {
                var abort = await pumping ?? throw Invalid();
                await Cancel(abort); return;
            }
            await terminal.Task;
            Status = "Delivering";
            try { await Retry(() => BattleFinalDelivery.Deliver(store, connection, default)); state = store.Read(); }
            catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
            {
                var abort = pumping.IsCompletedSuccessfully ? await pumping : null;
                abort ??= await Retry(() => BattleExecutorRunningPump.ReadAbortAsync(connection, default, tolerateTransient: false));
                if (abort is null) throw;
                running.Cancel();
                await pumping;
                await Cancel(abort);
            }
        }
        finally { running.Cancel(); await pumping; pump = null; }
    }
    private async Task Cancel(BattleCancellationWork work)
    {
        if(!state!.Assignment.Equals(work.Assignment) || state.Applied is not null)throw Invalid();
        BattleCleanupContracts.ValidateTerminal(work.Terminal);
        if (work.Terminal.AssignmentTransitionId != state.Assignment.TransitionId) throw Invalid();
        if (work.Terminal.Kind == BattleCleanupTerminalKind.StartedAbort)
        {
            if (state.PreparedManifest.IsEmpty) throw Invalid();
            var digest = state.Start?.ManifestDigest ?? ByteString.CopyFrom(
                BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest, state.PreparedManifest.Span));
            if (!work.Terminal.AbortedStartDigest.Equals(digest)) throw Invalid();
            if (controller is not null) await Native(() => { controller.StopForAbort(work.Terminal, digest); return true; });
        }
        else if (work.Terminal.Kind != BattleCleanupTerminalKind.PreStartCancellation || state.Start is not null
            || !state.FinalEnvelopeSha256.IsEmpty) throw Invalid();
        SaveState(s =>
        {
            if (work.Terminal.Kind == BattleCleanupTerminalKind.StartedAbort) s.AbortTerminal = work.Terminal.Clone();
            s.CancellationTransitionId = work.Terminal.TerminalTransitionId;
            s.Stage = BattleExecutorCheckpointStage.Cancelled;
        });
    }
    private async Task Cleanup()
    {
        Status="Cleaning";await StopHeartbeat();
        await Native(()=>{if(Current.ProgramState!=ProgramState.Entry || Current.Game is not null)GenScene.GoToMainMenu();return true;});await Entry();
        await Native(() =>
        {
            if (controller is null) return true;
            if (state!.AbortTerminal is { } abort) controller.ReleaseAbortedAtEntry(abort);
            else BattleTickController.ReleaseAtEntry(state.Applied);
            controller = null;
            return true;
        });
        SaveState(s=>s.Stage=BattleExecutorCheckpointStage.AtEntry);await Retry(async()=>{await connection.Heartbeat(false,default);return true;});
        BattleCleanupReceipt? cleaned=null;
        while(cleaned is null)
        {
            var challenge=await Retry(async()=>await connection.Api.CleanupChallengeAsync(new(){Executor=connection.Lease,Scope=state!.Assignment.Scope.Clone()},connection.Options(default)));
            var proof=await MainThread.Get(()=>
            {
                RequireEntry();var expected=state!.Applied is{} applied?new BattleCleanupTerminalBindingV1{Version=1,Kind=BattleCleanupTerminalKind.AppliedResult,TerminalTransitionId=applied.TransitionId,
                    AssignmentTransitionId=state.Assignment.TransitionId,CommittedFinalDigest=applied.CommittedFinalDigest}:state.AbortTerminal
                    ?? new(){Version=1,Kind=BattleCleanupTerminalKind.PreStartCancellation,TerminalTransitionId=state.CancellationTransitionId,AssignmentTransitionId=state.Assignment.TransitionId};
                if(!challenge.Scope.Equals(state.Assignment.Scope) || !challenge.Terminal.Equals(expected) || challenge.RuntimeInstanceId!=runtimeId || !challenge.InventorySha256.Equals(state.Assignment.InventorySha256))throw Invalid();
                return new BattleCleanupProofV1{Version=1,Scope=challenge.Scope.Clone(),ChallengeId=challenge.ChallengeId,Nonce=challenge.Nonce,CommittedFinalDigest=challenge.CommittedFinalDigest,
                    RuntimeInstanceId=runtimeId,InventorySha256=challenge.InventorySha256,AtEntry=true,Terminal=expected,AssignmentRejected=true,ProofKind=BattleCleanupProofKind.SameProcessEntry};
            });
            var request=new BattleCleanupRequest{Executor=connection.Lease,ClientRequestId=Guid.NewGuid().ToString("D"),Proof=proof};
            try{cleaned=await Retry(async()=>{request.Executor=connection.Lease;return await connection.Api.CleanupAsync(request,connection.Options(default));});}
            catch(RpcException error)when(error.StatusCode==StatusCode.FailedPrecondition){await RecoverTransport();}
            if(cleaned is not null)
            {
                BattleCleanupContracts.ValidateReceipt(cleaned);if(!cleaned.Scope.Equals(proof.Scope) || !cleaned.Terminal.Equals(proof.Terminal) || cleaned.RuntimeInstanceId!=runtimeId)throw Invalid();
            }
        }
        SaveState(s=>{s.Cleanup=cleaned.Clone();s.Stage=BattleExecutorCheckpointStage.Cleaned;});
        await MainThread.Get(()=>{RequireEntry();BattleGameContext.CompleteExecutor(cleaned);return true;});
        store.ArchiveCleaned();state=null;scenario=null;
        if(connection.Lease?.Binding?.Lane==BattleExecutorLane.Execution)
            await Retry(async()=>{await connection.Reconnect(await Binding(BattleExecutorLane.Execution),default);return true;});
        else
        {
            var old=connection;connection=new(settings,settings.ReadCredential());await Register(BattleExecutorLane.Execution);old.Dispose();
        }
        StartHeartbeat();
    }
    private BattleEntitySide[] Entities()=>profile.Profile.Fighters.Select(f=>new BattleEntitySide{FighterId=f.FighterId,Side=f.Side,EntityId=scenario!.Pawns[f.FighterId].ThingID}).ToArray();
    private BattleStartRequest StartRequest(ByteString manifest)
    {
        byte[] digest=BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,manifest.Span);
        string request=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("Rimlink.ExecutorStart/v1\0"+state!.Assignment.TransitionId+"\0"+Convert.ToHexStringLower(digest))).AsSpan(0,16)).ToString("D");
        return new(){ClientRequestId=request,Executor=connection.Lease,Scope=state.Assignment.Scope.Clone(),ManifestDigest=ByteString.CopyFrom(digest)};
    }
    private void SaveState(Action<BattleExecutorCheckpointV1> change)
    {var next=state?.Clone()??throw Invalid();change(next);store.Save(next);state=next;}
    private async Task VerifyRuntime()
    {var facts=await MainThread.Get(()=>BattleRuntimeCapture.Measure(settings));profile.VerifyRuntime(settings.Roots,facts);}
    private async Task<T> Native<T>(Func<T> work)
    {
        Interlocked.Increment(ref callbacks);
        try{return await MainThread.Get(()=>{if(state?.Stage is BattleExecutorCheckpointStage.AtEntry or BattleExecutorCheckpointStage.Cleaned)throw Invalid();return work();});}
        finally{Interlocked.Decrement(ref callbacks);}
    }
    private async Task Loaded()
    {long began=Stopwatch.GetTimestamp();while(!await MainThread.Get(()=>Current.ProgramState==ProgramState.Playing && Current.Game is{PlayerHasControl:true} && Find.CurrentMap is not null && !LongEventHandler.AnyEventNowOrWaiting)){if(Stopwatch.GetElapsedTime(began)>TimeSpan.FromMinutes(2))throw Invalid();await Task.Delay(100);}}
    private async Task Entry()
    {long began=Stopwatch.GetTimestamp();while(!await MainThread.Get(()=>Current.ProgramState==ProgramState.Entry && Current.Game is null && !LongEventHandler.AnyEventNowOrWaiting)){if(Stopwatch.GetElapsedTime(began)>TimeSpan.FromMinutes(2))throw Invalid();await Task.Delay(100);}}
    private void RequireEntry()
    {
        if(Current.ProgramState!=ProgramState.Entry || Current.Game is not null || LongEventHandler.AnyEventNowOrWaiting || BattleTickController.Current is not null
            || RimlinkSession.Active || RimlinkSession.Connection is not null || callbacks!=0 || pump?.PendingCallbacks>0)throw Invalid();
    }
    private static void NoLinks(string path)
    {for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw Invalid();}
    private static InvalidDataException Invalid()=>new("battle-executor-operator-recovery-required");
}
