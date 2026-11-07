using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Application.Sessions;
using System.Text;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed class AcceptanceRuntime : IAcceptanceControlTarget, IDisposable
{
    private readonly AcceptanceStand stand;
    private readonly AcceptanceComposition composition;
    private readonly AcceptanceRunIdentity identity;
    private readonly string evidence;
    private readonly AcceptanceRestartOrigin? restart;
    private readonly AcceptanceClockReading origin;
    private readonly AcceptanceClock clock;
    private AcceptanceClockRun? run;
    private readonly DateTimeOffset startUtc;
    private WebApplication? app;
    internal AcceptanceHold Replies { get; } = new();
    internal AcceptanceControlBinding Binding { get; }
    internal string BindingPath { get; }
    internal TimeProvider Clock => clock;
    internal bool Failed => run?.Failed == true;

    internal AcceptanceRuntime(AcceptanceStand stand, AcceptanceComposition composition, string? previousRun, TimeSpan downtime)
    {
        this.stand = stand; this.composition = composition;
        evidence = AcceptancePaths.Directory(Path.Combine(stand.Root, "evidence"));
        string id = Guid.NewGuid().ToString("D");
        if (previousRun is not null)
        {
            if (!Guid.TryParseExact(previousRun, "D", out var parsed) || parsed == Guid.Empty) throw new InvalidDataException("acceptance-restart-run-invalid");
            AcceptanceRestart.CheckTip(evidence, stand.StandId, previousRun);
            restart = AcceptanceRestart.Read(Path.Combine(evidence, "run-" + previousRun + ".clock.jsonl"), stand.StandId, previousRun, downtime);
        }
        else if (downtime != TimeSpan.Zero || Directory.EnumerateFiles(evidence, "run-*.clock.jsonl").Any())
            throw new InvalidDataException("acceptance-explicit-restart-required");
        startUtc = restart?.StartUtc ?? DateTimeOffset.UtcNow;
        Binding = new(1, stand.StandId, id, OutpostProductQualification.SourceIdentity, "rlo-" + Guid.Parse(id).ToString("N"),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        BindingPath = Path.Combine(evidence, "run-" + id + ".control.json");
        identity = new(stand.StandId, id, Environment.ProcessId, Binding.SourceIdentity);
        clock = new(TimeProvider.System, startUtc, composition);
        origin = clock.Read();
        try { WriteLaunchEvidence("request", new { Version = 1, Identity = identity, Origin = origin, Restart = restart }); }
        catch { clock.Dispose(); throw; }
    }

    internal async Task BindAsync(WebApplication application)
    {
        if (run is not null || app is not null) throw new InvalidOperationException("acceptance-runtime-already-bound");
        using var scope = application.Services.CreateScope();
        var checkpoints = await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Outposts.AsNoTracking().Select(o => o.CheckpointUtc).ToArrayAsync();
        AcceptanceRestart.CheckCheckpoints(startUtc, checkpoints);
        var actual = application.Services.GetRequiredService<OutpostClock>();
        var initial = clock.Initialization ?? throw new InvalidDataException("acceptance-clock-construction-not-observed");
        _ = AcceptanceClockRun.VerifySample(clock, actual, checked(initial.Utc.UtcTicks - initial.Timestamp));
        // Build has completed its startup checks. Publish before starting hosted services or the control listener.
        var journal = new FileStream(Path.Combine(evidence, "run-" + identity.RunId + ".clock.jsonl"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try { run = new(identity, clock, journal, restart, origin); }
        catch { journal.Dispose(); throw; }
        run.Bind(actual);
        using var file = new FileStream(BindingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, Binding, AcceptanceStandFiles.Json); file.Flush(flushToDisk: true);
        app = application;
    }

    internal void RecordStartupRefusal(Exception error)
    {
        run?.RefuseStartup();
        WriteLaunchEvidence("refusal", new
        {
            Version = 1, Identity = identity,
            ClockJournalCreated = File.Exists(Path.Combine(evidence, "run-" + identity.RunId + ".clock.jsonl")),
            ControlPublished = File.Exists(BindingPath), Error = error.GetType().Name,
            Reason = error.Message[..Math.Min(error.Message.Length, 2048)]
        });
    }

    private void WriteLaunchEvidence(string suffix, object evidenceValue)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(evidenceValue, AcceptanceStandFiles.Json);
        if (bytes.Length > 16 * 1024) throw new IOException("acceptance-launch-evidence-limit");
        using var file = new FileStream(Path.Combine(evidence, $"launch-{identity.RunId}.{suffix}.json"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        file.Write(bytes); file.Flush(flushToDisk: true); file.Position = 0;
        byte[] actual = new byte[bytes.Length]; file.ReadExactly(actual);
        if (!actual.AsSpan().SequenceEqual(bytes)) throw new IOException("acceptance-launch-evidence-readback-mismatch");
    }

    public ValueTask<AcceptanceControlReply> HandleAsync(AcceptanceControlCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var proof = composition.Observe();
        if (app is null || run is null || proof is not { AutomatedPrerequisitesVerified: true, IsolationVerified: true } || Failed)
            return ValueTask.FromResult(new AcceptanceControlReply(false, "acceptance-control-no-current-qualification"));
        object data;
        switch (command.Action)
        {
            case "observe":
                data = new { Binding.StandId, Binding.RunId, Binding.SourceIdentity, ProcessId = Environment.ProcessId,
                    stand.Port, stand.GameRoot, stand.HostGameId, stand.GuestGameId, stand.HostSaveData, stand.GuestSaveData,
                    Binaries = AcceptanceStandFiles.Read<AcceptancePrerequisites>(stand.PrerequisitesFile).Binaries, Sample = run.Sample(), Hold = Replies.Observe(),
                    Sessions = app.Services.GetRequiredService<SessionRegistry>().All.Select(s => new { s.PlayerId,
                        SessionSha256 = SessionHash(s.Token), s.SaveWriteEpoch, s.OutpostVersion }).ToArray() };
                break;
            case "advance":
                data = run.Advance(command.StepId ?? "", TimeSpan.FromTicks(command.DurationTicks), command.Reason ?? ""); break;
            case "stop":
                data = new { Sample = run.Sample() }; app.Lifetime.StopApplication(); break;
            case "arm-reply":
                RequireSession(command.Target);
                if (command.Boundary is not ("prepared-durable" or "committed-durable" or "prepare-received-before-service" or "commit-received-before-service")
                    || command.Target!.OperationId.Length == 0 || command.Target.Kind != "staff" || command.Target.IntentSha256.Length != 64)
                    throw new InvalidDataException("acceptance-reply-boundary-invalid");
                Replies.Arm(command.Boundary, command.Target); data = Replies.Observe()!; run.Record("ReplyArmed", data); break;
            case "release-reply":
                RequireSession(command.Target); Replies.Release(command.Target!, command.Drop); data = Replies.Observe()!; run.Record("ReplyReleased", data); break;
            case "disarm-reply":
                Replies.Disarm(); data = Replies.Observe() ?? (object)new { State = "Idle" }; run.Record("ReplyDisarmed", data); break;
            default: return ValueTask.FromResult(new AcceptanceControlReply(false, "acceptance-control-action-invalid"));
        }
        return ValueTask.FromResult(new AcceptanceControlReply(true, command.Action, JsonSerializer.SerializeToElement(data, AcceptanceStandFiles.Json)));
    }
    internal void RequireSession(AcceptanceFaultTarget? target)
    {
        if (target is null || app is null || composition.Observe() is not { AutomatedPrerequisitesVerified: true, IsolationVerified: true }
            || Failed || !app.Services.GetRequiredService<SessionRegistry>().All.Any(s => !s.Cts.IsCancellationRequested && s.OutpostVersion == 1
                && SessionHash(s.Token) == target.SessionSha256)) throw new InvalidDataException("acceptance-reply-current-session-required");
    }
    internal void RecordRpc(string kind, object data) => (run ?? throw new InvalidOperationException("acceptance-clock-not-bound")).Record(kind, data);
    internal static string SessionHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public void Dispose() { Replies.Disarm(); if (run is not null) run.Dispose(); else clock.Dispose(); }
}
