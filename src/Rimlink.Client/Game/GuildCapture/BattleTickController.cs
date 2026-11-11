using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

public sealed record BattleRuntimeControl(BattleFighterSide Side,string GrantId,long Epoch,bool Present,long ConnectionEpoch=0);

public sealed class BattleTickController
{
    public static BattleTickController? Current { get; private set; }
    private readonly BattleScenario scenario;
    private readonly BattlePreparedManifestV1 start;
    private readonly BattleTickGate gate;
    private readonly FileStream journal;
    private readonly BattleReceiptJournal receipts;
    private readonly BattleRunningInbox inbox;
    private readonly Dictionary<string,BattleControlCut> appliedCuts=[];
    private readonly long[] closedConnections=new long[2];
    private readonly Dictionary<(BattleFighterSide,long),BattleQueuedCommand> pending=[];
    private readonly Dictionary<(BattleFighterSide,long),(BattleQueuedCommand Command,BattleCommandReceipt Receipt)> executed=[];
    private readonly Dictionary<BattleFighterSide,BattleRuntimeControl> control=[];
    private readonly Action<BattleRuleState,BattlePawn[],TimeSpan,TimeSpan,long> completed;
    private byte[] tail=new byte[32];
    private bool finished,disposed,runtimeAcknowledged;
    private BattleCleanupTerminalBindingV1? abort;
    private BattleRuntimeStartedProofV1? runtimeProof;
    private int lastAiTick, lastViewTick = -1;
    public string? Failure { get; private set; }
    public BattleRuleState State=>gate.State;
    public event Action<BattleQueuedCommand,BattleCommandReceipt>? Acknowledged;
    public event Action<string>? CutApplied;

    public BattleTickController(BattleScenario scenario,BattleStartAuthorization authorization,string runtimeInstanceId,
        BattleExecutorSettings settings,BattleExecutorProfile profile,Action<BattleRuleState,BattlePawn[],TimeSpan,TimeSpan,long> completed)
    {
        start=BattleManifestContracts.ReadPrepared(authorization.Manifest.Span);this.scenario=scenario;this.completed=completed;
        if(Current is not null || !BattleGameContext.IsBattleGame || BattleGameContext.Role!=BattleMapRole.Executor || scenario.Map!=Find.CurrentMap
            || start.RuntimeInstanceId!=runtimeInstanceId || start.ExecutorId!=settings.ExecutorId || start.WorldInstanceId!=settings.WorldInstanceId
            || !start.ProfileDigest.Equals(profile.Digest) || !start.InitialSnapshot.Equals(scenario.InitialSnapshot)
            || !authorization.ManifestDigest.Span.SequenceEqual(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,authorization.Manifest.Span))
            || !authorization.Scope.Equals(BattleGameContext.Scope) || start.BattleId!=authorization.Scope.BattleId || start.WorldInstanceId!=authorization.Scope.WorldInstanceId
            || start.RestoreBoundary!=authorization.Scope.RestoreBoundary || start.Attempt!=authorization.Scope.Attempt || start.Fence!=authorization.Scope.Fence || authorization.StartedAtUnixMs<=0
            || !Guid.TryParseExact(authorization.TransitionId,"D",out _) || !Guid.TryParseExact(authorization.ReadyTransitionId,"D",out _)
            || !Guid.TryParseExact(authorization.AuthorityHeadId,"D",out _) || authorization.AuthorityHeadDigest.Length!=32
            || Find.TickManager.TicksGame!=start.InitialTick || !Verse.Current.Game.PlayerHasControl || Find.TickManager.ForcePaused)throw Invalid();
        foreach(var entity in start.Entities)if(scenario.Pawns[entity.FighterId].ThingID!=entity.EntityId)throw Invalid();
        scenario.VerifyInitial();profile.VerifyRuntime(settings.Roots,BattleRuntimeCapture.Measure(settings));
        gate=new(new(new(start.Arena.ZoneMinX,start.Arena.ZoneMinZ,start.Arena.ZoneMaxX,start.Arena.ZoneMaxZ),
            start.Entities.Select(e=>new BattleFighter(e.FighterId,(BattleSide)(int)e.Side)).ToArray(),start.InitialTick),TimeProvider.System);
        journal=new(scenario.ArtifactPath("simulation"),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        try
        {
            receipts=new(scenario.ArtifactPath("receipts"),authorization.ManifestDigest.ToByteArray());inbox=new(start);
            journal.Write("RLBGSJ01"u8);Append(new{Kind="start",ManifestDigest=authorization.ManifestDigest.ToByteArray(),StartTick=start.InitialTick,Pawns=scenario.ReadPawns()},true);
            Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;lastAiTick=start.InitialTick-120;Current=this;
        }
        catch{journal.Dispose();throw;}
    }
    internal BattleExecutorRetainedContextV1 RetainedContext(IReadOnlyList<string> pendingCuts,int callbacks)
    {
        if(Failure is not null || !BattleGameContext.IsBattleGame || !ReferenceEquals(Current,this) || callbacks is <0 or >64)throw Invalid();
        using var input=new FileStream(scenario.ArtifactPath("receipts"),FileMode.Open,FileAccess.Read,FileShare.Read);
        var value=new BattleExecutorRetainedContextV1{Version=1,Phase=finished?BattleExecutorRetainedPhase.Terminal:BattleExecutorRetainedPhase.Running,
            StartNonce=start.StartNonce,GameLoaded=true,TickRuntimeBound=true,CurrentTick=Find.TickManager.TicksGame,PendingCallbacks=callbacks,
            ReceiptJournalSha256=ByteString.CopyFrom(SHA256.HashData(input)),ReceiptJournalBytes=input.Length};value.PendingCutIds.Add(pendingCuts);return value;
    }
    public BattleRuntimeStartedProofV1 RuntimeProof(string startRequestId)
    {
        if(runtimeProof is not null){if(runtimeProof.StartRequestId!=startRequestId)throw Invalid();return runtimeProof.Clone();}
        if(runtimeAcknowledged || finished || disposed || Failure is not null || !BattleGameContext.IsBattleGame || Find.TickManager.TicksGame!=start.InitialTick)throw Invalid();
        using var input=new FileStream(scenario.ArtifactPath("receipts"),FileMode.Open,FileAccess.Read,FileShare.Read);
        runtimeProof=BattleRuntimeStartedContracts.Read(new BattleRuntimeStartedProofV1{Version=1,Scope=BattleGameContext.Scope!.Clone(),RuntimeInstanceId=start.RuntimeInstanceId,
            AssignmentTransitionId=start.PreparationTransitionId,StartDigest=ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray())),
            StartRequestId=startRequestId,InitialSnapshot=start.InitialSnapshot.Clone(),InitialTick=start.InitialTick,GameLoaded=true,TickRuntimeBound=true,
            ReceiptJournalSha256=ByteString.CopyFrom(SHA256.HashData(input)),ReceiptJournalBytes=input.Length}.ToByteArray());return runtimeProof.Clone();
    }
    public void Activate(BattleRuntimeStartedReply reply,long sentAtTimestamp)
    {
        if(runtimeProof is null || reply.ElapsedSinceStartMs<0 || !reply.ProofDigest.Span.SequenceEqual(SHA256.HashData(runtimeProof.ToByteArray())))throw Invalid();
        if(runtimeAcknowledged)return;
        if(finished || disposed || Failure is not null || !BattleGameContext.IsBattleGame || Find.TickManager.TicksGame!=start.InitialTick)throw Invalid();
        var prior=TimeSpan.FromMilliseconds(Math.Min(reply.ElapsedSinceStartMs,1200000))+Stopwatch.GetElapsedTime(sentAtTimestamp);
        gate.Start(start.InitialTick,defenderInitiallyAi:true,priorWallElapsed:prior<TimeSpan.FromMinutes(20)?prior:TimeSpan.FromMinutes(20));
        runtimeAcknowledged=true;Find.TickManager.CurTimeSpeed=TimeSpeed.Normal;
    }
    public void ApplyRunningWork(BattleRunningWork work)
    {
        if(Failure is not null)throw Invalid();
        try{inbox.Apply(work,Enqueue,ApplyCut,rows=>Contact(rows.Select(c=>new BattleRuntimeControl(c.Side,c.GrantId,c.ControlEpoch,c.Present,c.ConnectionEpoch)).ToArray()));}
        catch(Exception error){Fail(error);throw;}
    }

    internal ByteString CaptureView()
    {
        if (!runtimeAcknowledged || finished || Failure is not null || lastViewTick == State.Tick) return ByteString.Empty;
        var frame = new BattleRunningViewV1
        {
            Version = 1, Scope = BattleGameContext.Scope!.Clone(), StartDigest = runtimeProof!.StartDigest,
            Sequence = (long)State.Tick - start.InitialTick + 1, Tick = State.Tick, HeldTicks = State.HeldTicks
        };
        var pawns = scenario.ReadPawns().ToDictionary(p => p.Id);
        foreach (var entity in start.Entities)
        {
            var state = pawns[entity.FighterId];
            var native = scenario.Pawns[entity.FighterId];
            frame.Pawns.Add(new BattleRunningPawn
            {
                Pawn = new() { FighterId = entity.FighterId, EntityId = entity.EntityId, Side = entity.Side,
                    X = state.X, Z = state.Z, Alive = state.Alive, Downed = state.Downed, Mental = state.Mental,
                    CanFight = state.CanFight, Health = state.Health },
                Spawned = native.Spawned, Drafted = native.drafter?.Drafted == true, Rotation = native.Rotation.AsInt
            });
        }
        var bytes = frame.ToByteString();
        _ = BattleManifestContracts.ReadRunningView(bytes.Span, start);
        lastViewTick = frame.Tick;
        return bytes;
    }
    private void ApplyCut(BattleControlCut cut)
    {
        if(appliedCuts.TryGetValue(cut.CutId,out var old)){if(!old.Equals(cut))throw Invalid();CutApplied?.Invoke(cut.CutId);return;}
        int index=(int)cut.Side-1;
        if(cut.ThroughConnectionEpoch<=closedConnections[index])throw Invalid();
        foreach(var item in pending.Where(p=>p.Value.Side==cut.Side && p.Value.ConnectionEpoch<=cut.ThroughConnectionEpoch).ToArray())
            Record(item.Value,new(){Side=cut.Side,Sequence=item.Value.Sequence,CommandDigest=item.Value.CommandDigest,
                Disposition=BattleCommandDisposition.NotExecuted,Reason=BattleCommandReason.ControlRevoked});
        receipts.AppendCut(cut.CutId);closedConnections[index]=cut.ThroughConnectionEpoch;appliedCuts.Add(cut.CutId,cut.Clone());CutApplied?.Invoke(cut.CutId);
    }
    private void Record(BattleQueuedCommand command,BattleCommandReceipt receipt)
    {
        if(!finished)Append(new{Kind="ack",Receipt=receipt.ToByteArray()},true);
        receipts.Append(receipt);var key=(command.Side,command.Sequence);executed.Add(key,(command.Clone(),receipt.Clone()));pending.Remove(key);
        Acknowledged?.Invoke(command.Clone(),receipt.Clone());
    }
    // The authenticated transport supplies the current server control cut, never the player payload.
    public void Contact(IReadOnlyList<BattleRuntimeControl> cut)
    {
        if(Failure is not null || finished)return;
        if(cut.Count>2 || cut.Select(c=>c.Side).Distinct().Count()!=cut.Count || cut.Any(c=>c.Side is not (BattleFighterSide.Attacker or BattleFighterSide.Defender)
            || c.ConnectionEpoch<0 || c.ConnectionEpoch==0 && (c.Present || c.GrantId!="" || c.Epoch!=0)
            || c.ConnectionEpoch>0 && (!Guid.TryParseExact(c.GrantId,"D",out _) || c.Epoch<=0)))throw Invalid();
        control.Clear();foreach(var row in cut)control.Add(row.Side,row);
        gate.Contact(control.GetValueOrDefault(BattleFighterSide.Attacker)?.Present==true,control.GetValueOrDefault(BattleFighterSide.Defender)?.Present==true);
    }
    public void TransportFailed()=>gate.TransportFailed();
    public void Enqueue(BattleQueuedCommand value)
    {
        if(Failure is not null || value.ConnectionEpoch<=0 || !Guid.TryParseExact(value.DeliveryId,"D",out _) || value.Sequence is <1 or >24000 || value.ControlEpoch<=0 || !Guid.TryParseExact(value.GrantId,"D",out _)
            || value.CommandDigest.Length!=32 || !SHA256.HashData(value.Command.Span).AsSpan().SequenceEqual(value.CommandDigest.Span))throw Invalid();
        _=BattleCommandContracts.Read(value.Command.Span,start,value.Side);var key=(value.Side,value.Sequence);
        if(executed.TryGetValue(key,out var old)){if(!old.Command.Equals(value))throw Invalid();Acknowledged?.Invoke(old.Command.Clone(),old.Receipt.Clone());return;}
        if(pending.TryGetValue(key,out var queued)){if(!queued.Equals(value))throw Invalid();return;}
        if(finished || value.ConnectionEpoch<=closedConnections[(int)value.Side-1])
        {
            Record(value,new(){Side=value.Side,Sequence=value.Sequence,CommandDigest=value.CommandDigest,Disposition=BattleCommandDisposition.NotExecuted,
                Reason=finished?BattleCommandReason.Terminal:BattleCommandReason.ControlRevoked});return;
        }
        if(pending.Count(p=>p.Key.Item1==value.Side)>=64 || executed.Count+pending.Count>=48000)throw Invalid();pending.Add(key,value.Clone());
    }
    public void Frame()
    {
        if(Failure is not null || finished || !runtimeAcknowledged)return;
        try
        {
            if(RimlinkSession.Active || RimlinkSession.Connection is not null || !BattleGameContext.IsBattleGame || scenario.Map!=Find.CurrentMap)throw Invalid();
            gate.Pump();if(!gate.Running)Finish();
            else if(Find.TickManager.CurTimeSpeed!=TimeSpeed.Normal && Verse.Current.Game.PlayerHasControl && !Find.TickManager.ForcePaused)
                Find.TickManager.CurTimeSpeed=TimeSpeed.Normal;
        }
        catch(Exception error){Fail(error);}
    }
    internal bool BeforeTick()
    {
        if(Failure is not null || finished || !runtimeAcknowledged)return false;
        try
        {
            if(!BattleGameContext.IsBattleGame || scenario.Map!=Find.CurrentMap || RimlinkSession.Connection is not null || RimlinkSession.Active)throw Invalid();
            if(!gate.BeforeTick(Find.TickManager.TicksGame)){if(!gate.Running)Finish();return false;}
            foreach(var item in pending.OrderBy(p=>p.Key.Item2).ThenBy(p=>p.Key.Item1).Take(8).ToArray())
            {
                var command=item.Value;var current=control.GetValueOrDefault(command.Side);
                bool allowed=current is {Present:true} && current.GrantId==command.GrantId && current.Epoch==command.ControlEpoch && current.ConnectionEpoch==command.ConnectionEpoch
                    && command.ConnectionEpoch>closedConnections[(int)command.Side-1];
                Append(new{Kind="command",Command=command.ToByteArray(),Tick=State.Tick},true);
                var reason=allowed?BattleOrders.Apply(scenario,start,command.Side,BattleCommandContracts.Read(command.Command.Span,start,command.Side)):BattleCommandReason.ControlRevoked;
                var receipt=new BattleCommandReceipt{Side=command.Side,Sequence=command.Sequence,CommandDigest=command.CommandDigest,
                    Disposition=!allowed?BattleCommandDisposition.NotExecuted:reason==BattleCommandReason.None?BattleCommandDisposition.Executed:BattleCommandDisposition.Refused,Reason=reason};
                if(allowed)receipt.AcceptedTick=State.Tick-start.InitialTick;
                Record(command,receipt);
            }
            if(State.Tick-lastAiTick>=120)
            {
                lastAiTick=State.Tick;
                foreach(var side in new[]{BattleSide.Attacker,BattleSide.Defender})if(gate.UseAi(side))
                {Append(new{Kind="ai",Side=side,Tick=State.Tick},true);BattleOrders.Fallback(scenario,start,(BattleFighterSide)(int)side);}
            }
            return true;
        }
        catch(Exception error){Fail(error);return false;}
    }
    internal void AfterTick()
    {
        if(Failure is not null || finished)return;
        try
        {
            var pawns=scenario.ReadPawns();var state=gate.AfterTick(Find.TickManager.TicksGame,pawns);
            AppendBytes(BattleSimulationTick.Encode(state,pawns),false);if(state.Outcome!=BattleOutcome.Ongoing)Finish();
        }
        catch(Exception error){Fail(error);}
    }
    internal void Fail(Exception error)
    {
        if(Failure is not null)return;Failure=error.GetType().Name;finished=true;
        if(!disposed){journal.Dispose();disposed=true;}
        if(Verse.Current.ProgramState==ProgramState.Playing)Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
        RimlinkLog.Instance.Warn("guild battle native runtime requires recovery: "+Failure);
    }
    private void Finish()
    {
        if(finished)return;finished=true;Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
        foreach(var command in pending.Values.ToArray())Record(command,new(){Side=command.Side,Sequence=command.Sequence,CommandDigest=command.CommandDigest,
            Disposition=BattleCommandDisposition.NotExecuted,Reason=BattleCommandReason.Terminal});
        var pawns=scenario.ReadPawns();Append(new{Kind="terminal",State,Pawns=pawns},true);long size=journal.Length;journal.Dispose();disposed=true;
        completed(State,pawns,gate.EvaluatedWallElapsed,gate.EvaluatedTransportLostElapsed,size);
    }
    private void Append<T>(T value,bool flush)
    {
        AppendBytes(JsonSerializer.SerializeToUtf8Bytes(value),flush);
    }
    private void AppendBytes(byte[] bytes,bool flush)
    {
        if(bytes.Length is <1 or >16384 || journal.Length+bytes.Length+36>67108864)throw Invalid();
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData(tail);hash.AppendData(bytes);byte[] digest=hash.GetHashAndReset();
        Span<byte> count=stackalloc byte[4];BinaryPrimitives.WriteInt32LittleEndian(count,bytes.Length);journal.Write(count);journal.Write(bytes);journal.Write(digest);
        if(flush)journal.Flush(true);tail=digest;
    }
    public static void ReleaseAtEntry(BattleAppliedReceipt receipt)
    {
        var current=Current??throw Invalid();
        if(!current.finished || !current.disposed || current.Failure is not null || Verse.Current.ProgramState!=ProgramState.Entry || Verse.Current.Game is not null
            || receipt.BattleId!=current.start.BattleId || receipt.WorldInstanceId!=current.start.WorldInstanceId || receipt.RestoreBoundary!=current.start.RestoreBoundary
            || receipt.Attempt!=current.start.Attempt || receipt.Fence!=current.start.Fence || receipt.CommittedFinalDigest.Length!=32)throw Invalid();
        Current=null;
    }
    internal void StopForAbort(BattleCleanupTerminalBindingV1 terminal, ByteString manifestDigest)
    {
        BattleCleanupContracts.ValidateTerminal(terminal);
        if (!ReferenceEquals(Current, this) || !BattleGameContext.IsBattleGame || terminal.Kind != BattleCleanupTerminalKind.StartedAbort
            || terminal.AssignmentTransitionId != start.PreparationTransitionId || !terminal.AbortedStartDigest.Equals(manifestDigest)
            || abort is not null && !abort.Equals(terminal)) throw Invalid();
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        finished = true;
        if (!disposed) { journal.Flush(true); journal.Dispose(); disposed = true; }
        abort = terminal.Clone();
    }
    internal void ReleaseAbortedAtEntry(BattleCleanupTerminalBindingV1 terminal)
    {
        if (!ReferenceEquals(Current, this) || abort is null || !abort.Equals(terminal) || !finished || !disposed
            || Verse.Current.ProgramState != ProgramState.Entry || Verse.Current.Game is not null) throw Invalid();
        Current = null;
    }
    private static InvalidDataException Invalid()=>new("battle-native-tick-runtime-invalid");
}
