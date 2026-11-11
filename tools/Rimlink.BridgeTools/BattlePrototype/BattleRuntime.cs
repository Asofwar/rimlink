using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using RimWorld;
using Rimlink.BattlePrototype;
using Rimlink.Client.Game;
using Rimlink.Client.UI;
using Verse;

namespace Rimlink.BridgeTools.BattlePrototype;

internal sealed class BattleRuntime
{
    internal static BattleRuntime? Current { get; private set; }
    internal BattleBinding Binding { get; }
    internal BattleScenario? Scenario { get; private set; }
    internal BattleWireReply? Latest { get; private set; }
    internal BattleView? View { get; private set; }
    internal string Phase { get; private set; } = "Bound";
    internal string? Error { get; private set; }
    internal bool Running { get; private set; }
    internal long NextSequence { get; private set; } = 1;
    private readonly Harmony harmony;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentQueue<(BattleWireRequest Request, TaskCompletionSource<BattleWireReply> Completion)> outgoing = new();
    private readonly Dictionary<(BattleSide, long), BattleCommandAck> executed = new();
    private readonly Dictionary<(BattleSide, long), QueuedBattleCommand> commands = new();
    private BattleRules? rules;
    private BattleRuleState? terminal;
    private BattleFinalResult? final;
    private FileStream? journal;
    private string journalHash = new('0', 64);
    private long viewSequence, lastReply = Stopwatch.GetTimestamp(), startedAt, lastAiTick;
    private int startTick;
    private long bytesSent, bytesReceived, viewsSent, maxReplyBytes;
    private int maxBacklog, fallbackCalls;
    private volatile BattleView? pendingView;
    private bool networkPaused, stopped, finalQueued, controllerPending;

    private BattleRuntime(BattleBinding binding)
    {
        Binding = binding; harmony = new("rimlink.battle-prototype." + binding.Identity.BattleId.ToString("N"));
    }

    internal static async Task<BattleRuntime> Bind(string file, bool hasSession, string savedata)
    {
        if (Current is not null) throw new InvalidDataException("stop the previous prototype binding first");
        BattleFixture.CheckLocalPath(file);
        if (new FileInfo(file).Length is <= 0 or > BattleFrames.RequestLimit) throw new InvalidDataException("binding size invalid");
        var binding = JsonSerializer.Deserialize<BattleBinding>(await File.ReadAllBytesAsync(file), BattleContract.Json)
            ?? throw new InvalidDataException("empty binding");
        BattleFixture.CheckLocalPath(binding.StandRoot); BattleFixture.CheckLocalPath(binding.GameRoot); BattleFixture.CheckLocalPath(binding.SavedataRoot);
        BattleFixture.CheckLocalPath(typeof(Pawn).Assembly.Location);
        BattleFixture.CheckLocalPath(typeof(RimlinkSession).Assembly.Location);
        BattleFixture.CheckLocalPath(typeof(BattleRuntime).Assembly.Location);
        BattleFixture.CheckBinding(binding, file, Environment.ProcessPath!, savedata,
            BattleScenario.Hash(typeof(Pawn).Assembly.Location), BattleScenario.Hash(typeof(RimlinkSession).Assembly.Location),
            BattleScenario.Hash(typeof(BattleRuntime).Assembly.Location), hasSession);
        var reply = await BattleFrames.Request(binding, new("poll", binding.Capability, binding.Identity));
        if (!reply.Success) throw new InvalidDataException("coordinator refused this binding");
        var runtime = new BattleRuntime(binding) { Latest = reply, View = reply.View, NextSequence = reply.NextSequence };
        if (binding.Role == BattleRole.Arbiter && reply.Ledger?.Phase != BattlePhase.Prepared)
        {
            string envelope = Path.Combine(BattleFixture.MatchRoot(binding), "final-envelope.json");
            BattleFixture.CheckLocalPath(envelope);
            if (reply.Ledger?.Phase is not (BattlePhase.Started or BattlePhase.Applied or BattlePhase.TechnicalAbort)
                || !File.Exists(envelope) || new FileInfo(envelope).Length > BattleFrames.RequestLimit)
                throw new InvalidDataException("interrupted arbiter requires explicit recovery; simulation cannot be resumed");
            var result = JsonSerializer.Deserialize<BattleFinalResult>(File.ReadAllBytes(envelope), BattleContract.Json)
                ?? throw new InvalidDataException("empty final envelope");
            if (result.Identity != binding.Identity || result.Snapshot is null || result.Journal is null
                || result.Snapshot.Kind != BattleArtifactKind.FinalSnapshot || result.Journal.Kind != BattleArtifactKind.Journal)
                throw new InvalidDataException("final envelope differs from binding");
            foreach (var artifact in new[] { result.Snapshot, result.Journal })
            {
                string path = BattleFixture.ArtifactPath(binding, artifact.Kind); BattleFixture.CheckLocalPath(path);
                if (new FileInfo(path).Length != artifact.Bytes || BattleScenario.Hash(path) != artifact.Sha256)
                    throw new InvalidDataException("final evidence is incomplete; explicit recovery required");
            }
            string initial = BattleFixture.ArtifactPath(binding, BattleArtifactKind.InitialSnapshot); BattleFixture.CheckLocalPath(initial);
            if (BattleScenario.Hash(initial) != result.InitialSnapshotSha256) throw new InvalidDataException("initial evidence differs");
            runtime.final = result; runtime.Phase = "ResendingFinal";
        }
        return runtime;
    }

    internal void Activate()
    {
        if (RimlinkSession.Active || RimlinkSession.Connection is not null || Current is not null) throw new InvalidDataException("player session or binding appeared");
        Current = this;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.Update)), postfix: new HarmonyMethod(typeof(BattleRuntime), nameof(Frame)));
            if (Binding.Role == BattleRole.Arbiter)
            {
                harmony.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
                    prefix: new HarmonyMethod(typeof(BattleRuntime), nameof(BeforeTick)), postfix: new HarmonyMethod(typeof(BattleRuntime), nameof(AfterTick)),
                    finalizer: new HarmonyMethod(typeof(BattleRuntime), nameof(TickFailed)));
                harmony.Patch(AccessTools.Method(typeof(Storyteller), nameof(Storyteller.StorytellerTick)),
                    prefix: new HarmonyMethod(typeof(BattleRuntime), nameof(AllowStoryteller)));
            }
            if (final is not null) { _ = Send(new("final", Binding.Capability, Binding.Identity, Result: final)); finalQueued = true; }
            _ = Task.Run(NetworkLoop);
        }
        catch { Stop(); throw; }
    }

    internal void BeginPrepare()
    {
        if (Binding.Role != BattleRole.Arbiter || Phase != "Bound") throw new InvalidDataException("fresh arbiter binding required");
        Scenario = new(Binding); Phase = "LoadingTemplate"; Scenario.LoadTemplate();
    }
    internal void BuildArena() { Scenario!.CreateArena(); Phase = "ReloadingInitial"; }
    internal void FinishPrepare()
    {
        Scenario!.AttachReloaded();
        startTick = Find.TickManager.TicksGame;
        rules = new(BattleFixture.Zone, startTick);
        string journalPath = BattleFixture.ArtifactPath(Binding, BattleArtifactKind.Journal); BattleFixture.CheckLocalPath(journalPath);
        journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        Append(new { kind = "initial", Binding.Identity, snapshot = Scenario.InitialArtifact, recipe = Scenario.Recipe(), tick = rules.State.Tick });
        View = MakeView(); pendingView = null; Phase = "Ready";
    }

    internal async Task Start()
    {
        if (Phase != "Ready" || View is null || Scenario?.InitialArtifact is null) throw new InvalidDataException("arena is not ready");
        if (RimlinkSession.Active || RimlinkSession.Connection is not null || Find.CurrentMap != Scenario.Map || Find.TickManager.TicksGame != startTick
            || !Verse.Current.Game.PlayerHasControl || Find.TickManager.ForcePaused)
            throw new InvalidDataException("initial game context changed before start");
        var startView = View with { Sequence = 0 };
        var reply = await Send(new("start", Binding.Capability, Binding.Identity, Start: new(Binding.Identity, Scenario.InitialArtifact, startView)));
        if (!reply.Success) throw new InvalidDataException(reply.Code);
        MainThread.Post(() =>
        {
            if (stopped) return;
            if (!Verse.Current.Game.PlayerHasControl || Find.TickManager.ForcePaused) { Freeze("initial game control changed"); return; }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Normal) { Freeze("engine refused battle start speed"); return; }
            Running = true; Phase = "Running"; startedAt = Stopwatch.GetTimestamp(); lastReply = startedAt;
        });
    }

    internal Task<BattleWireReply> Order(BattleCommand command)
    {
        if (Binding.Role == BattleRole.Arbiter || Latest?.Ledger?.Phase != BattlePhase.Started)
            throw new InvalidDataException("running side controller required");
        if (command.Sequence != NextSequence || controllerPending) throw new InvalidDataException("controller sequence is pending");
        controllerPending = true;
        return CompleteOrder(Send(new("command", Binding.Capability, Binding.Identity, Command: command)));
    }

    private async Task<BattleWireReply> CompleteOrder(Task<BattleWireReply> operation)
    {
        try
        {
            var reply = await operation;
            MainThread.Post(() => { if (reply.Success) NextSequence = Math.Max(NextSequence, reply.NextSequence); controllerPending = false; });
            return reply;
        }
        catch { MainThread.Post(() => controllerPending = false); throw; }
    }

    private Task<BattleWireReply> Send(BattleWireRequest request)
    {
        if (stopped) throw new InvalidDataException("prototype is stopped");
        var completion = new TaskCompletionSource<BattleWireReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        outgoing.Enqueue((request, completion)); return completion.Task;
    }

    private async Task NetworkLoop()
    {
        long sentView = 0;
        while (!lifetime.IsCancellationRequested)
        {
            bool queued = outgoing.TryPeek(out var item);
            var view = pendingView;
            var request = queued ? item.Request : view is not null && view.Sequence > sentView
                ? new BattleWireRequest("view", Binding.Capability, Binding.Identity, View: view)
                : new BattleWireRequest("poll", Binding.Capability, Binding.Identity);
            try
            {
                var reply = await BattleFrames.Request(Binding, request, lifetime.Token);
                Interlocked.Add(ref bytesSent, 4 + JsonSerializer.SerializeToUtf8Bytes(request, BattleContract.Json).Length);
                int received = 4 + JsonSerializer.SerializeToUtf8Bytes(reply, BattleContract.Json).Length;
                Interlocked.Add(ref bytesReceived, received); maxReplyBytes = Math.Max(maxReplyBytes, received);
                if (queued) { outgoing.TryDequeue(out _); item.Completion.TrySetResult(reply); }
                if (request.View is not null && reply.Success) { sentView = request.View.Sequence; Interlocked.Increment(ref viewsSent); }
                MainThread.Post(() => Receive(reply));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or JsonException)
            {
                if (lifetime.IsCancellationRequested) return;
                MainThread.Post(() => { if (!stopped) Error = "coordinator transport unavailable"; });
            }
            try { await Task.Delay(queued ? 10 : 100, lifetime.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private void Receive(BattleWireReply reply)
    {
        if (stopped) return;
        if (!reply.Success)
        {
            if (Binding.Role == BattleRole.Arbiter) Freeze("coordinator refused arbiter operation: " + reply.Code);
            else Error = reply.Code;
            return;
        }
        lastReply = Stopwatch.GetTimestamp(); Latest = reply;
        if (Phase != "RecoveryRequired") Error = null;
        if (Binding.Role != BattleRole.Arbiter)
        {
            if (reply.View is { } view && (View is null || view.Sequence > View.Sequence)) View = view;
            NextSequence = Math.Max(NextSequence, reply.NextSequence);
        }
        else
        {
            foreach (var command in reply.Commands ?? [])
                if (!executed.ContainsKey((command.Side, command.Command.Sequence))) commands.TryAdd((command.Side, command.Command.Sequence), command);
            maxBacklog = Math.Max(maxBacklog, commands.Count);
            if (reply.Receipt is not null)
            {
                if (reply.View is { } accepted && (View is null || accepted.Sequence >= View.Sequence)) View = accepted;
                Phase = "ReceiptCommitted"; finalQueued = false; Error = null;
            }
            else if (reply.Ledger?.Phase is BattlePhase.TechnicalAbort or BattlePhase.RecoveryRequired)
                Abort(BattleEndReason.InvalidState, "coordinator revoked the battle");
        }
    }

    private static void Frame() => Current?.Pump();
    private void Pump()
    {
        if (stopped || !Running || terminal is not null) return;
        if (RimlinkSession.Active || RimlinkSession.Connection is not null) { Freeze("player session appeared"); return; }
        if (Stopwatch.GetElapsedTime(lastReply) >= TimeSpan.FromSeconds(60)) { Abort(BattleEndReason.TransportLost, "coordinator grace expired"); return; }
        if (rules!.CheckWallClock(Stopwatch.GetElapsedTime(startedAt)).Outcome != BattleOutcome.Ongoing) { Finish(rules.State); return; }
        if (networkPaused && Stopwatch.GetElapsedTime(lastReply) < TimeSpan.FromSeconds(1))
        { networkPaused = false; Find.TickManager.CurTimeSpeed = TimeSpeed.Normal; }
    }

    private static bool BeforeTick(ref bool __state)
    {
        var current = Current;
        if (current is null || current.Binding.Role != BattleRole.Arbiter) return true;
        __state = false;
        if (!current.Running || current.terminal is not null) return false;
        try
        {
            current.Pump();
            if (!current.Running || current.terminal is not null) return false;
            if (Stopwatch.GetElapsedTime(current.lastReply) >= TimeSpan.FromSeconds(1))
            {
                current.networkPaused = true; Find.TickManager.CurTimeSpeed = TimeSpeed.Paused; return false;
            }
            if (Find.CurrentMap != current.Scenario!.Map) { current.Freeze("battle map was replaced"); return false; }
            foreach (var pair in current.commands.OrderBy(p => p.Key.Item2).ThenBy(p => p.Key.Item1).Take(8).ToArray())
            {
                var command = pair.Value; var key = pair.Key;
                if (!current.executed.TryGetValue(key, out var ack))
                {
                    string? error = BattleOrders.Apply(current.Scenario, command.Side, command.Command);
                    current.Append(new { kind = "command", command, tick = Find.TickManager.TicksGame, success = error is null, error });
                    ack = new(command.Side, command.Command.Sequence, command.Digest, Find.TickManager.TicksGame, error is null, error, current.journalHash);
                    current.executed.Add(key, ack);
                }
                _ = current.Send(new("ack", current.Binding.Capability, current.Binding.Identity, Ack: ack));
                current.commands.Remove(key);
            }
            if (Find.TickManager.TicksGame - current.lastAiTick >= 120)
            {
                current.lastAiTick = Find.TickManager.TicksGame;
                foreach (var side in new[] { BattleSide.Attacker, BattleSide.Defender })
                    if (side == BattleSide.Attacker ? current.Latest?.AttackerOnline == false : current.Latest?.DefenderOnline == false)
                    {
                        current.Append(new { kind = "fallback", side, tick = Find.TickManager.TicksGame });
                        current.fallbackCalls++; BattleOrders.Fallback(current.Scenario, side);
                    }
            }
            __state = true; return true;
        }
        catch (Exception ex) { current.Abort(BattleEndReason.InvalidState, ex.Message); return false; }
    }

    private static void AfterTick(bool __state)
    {
        if (!__state || Current is not { } current || current.terminal is not null) return;
        try
        {
            var pawns = current.Scenario!.ReadPawns();
            var state = current.rules!.Step(Find.TickManager.TicksGame, pawns);
            current.Append(new { kind = "tick", state, pawns }, flush: false);
            if (state.Outcome != BattleOutcome.Ongoing) current.Finish(state);
            else { current.View = current.MakeView(); current.pendingView = current.View; }
        }
        catch (Exception ex) { current.Abort(BattleEndReason.InvalidState, ex.Message); }
    }
    private static Exception? TickFailed(Exception? __exception)
    {
        if (__exception is not null) Current?.Abort(BattleEndReason.InvalidState, __exception.Message);
        return __exception;
    }
    private static bool AllowStoryteller() => Current is not { Binding.Role: BattleRole.Arbiter };

    private BattleView MakeView() => new(Binding.Identity, ++viewSequence, startTick,
        terminal ?? rules!.State, BattleFixture.Arena, BattleFixture.Zone, Scenario!.ReadPawns());

    private void Append<T>(T value, bool flush = true)
    {
        var entry = new { previous = journalHash, body = value };
        journalHash = BattleContract.Digest(entry);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { hash = journalHash, entry }, BattleContract.Json) + "\n");
        journal!.Write(bytes); if (flush) journal.Flush(flushToDisk: true);
    }

    private void Finish(BattleRuleState decision)
    {
        if (terminal is not null) return;
        terminal = decision; Running = false; Phase = "Terminal"; Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        try
        {
            Append(new { kind = "terminal", decision }); journal!.Dispose(); journal = null;
            var snapshot = Scenario!.SaveFinal();
            final = new(Binding.Identity, decision, Scenario.InitialArtifact!.Sha256, snapshot,
                Scenario.Describe(BattleArtifactKind.Journal), Scenario.ReadPawns());
            string envelope = Path.Combine(BattleFixture.MatchRoot(Binding), "final-envelope.json");
            BattleFixture.CheckLocalPath(envelope);
            using (var stream = new FileStream(envelope, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { JsonSerializer.Serialize(stream, final, BattleContract.Json); stream.Flush(flushToDisk: true); }
            BattleFixture.CrashIfArmed(BattleFixture.MatchRoot(Binding), "after-final-durable");
            View = MakeView(); pendingView = null;
            _ = Send(new("view", Binding.Capability, Binding.Identity, View: View));
            _ = Send(new("final", Binding.Capability, Binding.Identity, Result: final)); finalQueued = true;
        }
        catch (Exception ex) { Error = "final evidence failed: " + ex.Message; Phase = "RecoveryRequired"; }
    }

    internal void Abort(BattleEndReason reason, string error)
    {
        Error = error;
        if (rules is not null && Running) Finish(rules.State with { Outcome = BattleOutcome.TechnicalAbort, Reason = reason });
        else if (Binding.Role == BattleRole.Arbiter && Verse.Current.ProgramState == ProgramState.Playing) Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
    }

    private void Freeze(string error)
    {
        Error = error; Running = false; Phase = "RecoveryRequired";
        if (Binding.Role == BattleRole.Arbiter && Verse.Current.ProgramState == ProgramState.Playing)
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
    }

    internal void Stop()
    {
        if (stopped) return;
        Abort(BattleEndReason.TransportLost, "prototype stopped");
        stopped = true; Running = false; lifetime.Cancel(); harmony.UnpatchAll(harmony.Id); journal?.Dispose(); journal = null;
        while (outgoing.TryDequeue(out var item)) item.Completion.TrySetCanceled();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    internal object Status() => new
    {
        success = true, role = Binding.Role.ToString(), Binding.Identity.BattleId, phase = Phase, error = Error,
        playerHasControl = Verse.Current.Game?.PlayerHasControl,
        forcePaused = Verse.Current.ProgramState == ProgramState.Playing ? Find.TickManager.ForcePaused : (bool?)null,
        running = Running, hasSession = RimlinkSession.Active, processId = Environment.ProcessId,
        tick = rules?.State.Tick, view = View, nextSequence = NextSequence, backlog = commands.Count,
        executed = executed.Count, finalQueued, receipt = Latest?.Receipt, acks = Latest?.Acks,
        maxBacklog, fallbackCalls, attackerOnline = Latest?.AttackerOnline, defenderOnline = Latest?.DefenderOnline,
        outgoing = outgoing.Count, networkPaused, silenceSeconds = Stopwatch.GetElapsedTime(lastReply).TotalSeconds,
        bytesSent = Interlocked.Read(ref bytesSent), bytesReceived = Interlocked.Read(ref bytesReceived),
        viewsSent = Interlocked.Read(ref viewsSent), maxReplyBytes = Interlocked.Read(ref maxReplyBytes),
        recipe = Scenario?.Pawns.Count == 8 ? Scenario.Recipe() : null
    };

}
