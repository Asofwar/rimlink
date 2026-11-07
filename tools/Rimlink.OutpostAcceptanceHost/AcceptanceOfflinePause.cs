using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed record AcceptanceOfflineIdentity(string StandId, string RunId, int ProcessId, DateTime ProcessStartedUtc,
    string SourceIdentity, string Action, string RestoreId, string StandDigest, string InvocationSha256, string RequestCommitment);
internal sealed record AcceptanceOfflineJournal(string RestoreId, string IntentSha256, string Phase, long Revision, string JournalSha256, string[] Paths);
internal sealed record AcceptanceOfflineState(AcceptanceOfflineArm Arm, string Status, int Occurrence, long Sequence, string? EventSha256, JsonElement? Evidence, string? Error);

internal sealed class AcceptanceOfflinePause : IDisposable
{
    private static readonly string[] FileHooks = ["AfterStageFile", "AfterRetainFile", "AfterSwitchPlanned", "AfterSwitchEffect", "AfterSwitchCompleted",
        "AfterBackoutCurrentFile", "AfterBackoutPlanned", "AfterBackoutEffect", "AfterBackoutCompleted"];
    private static readonly string[] Hooks = ["AfterJournalCreated", "AfterExternalFence", "AfterStagedVerified", "AfterOldSetRetained", "AfterInstalledVerified",
        "AfterMainPlanWritten", "AfterMainPlanBound", "AfterMainPrepared", "BeforeSchemaMigration", "AfterSchemaMigration", "AfterMainDatabaseCommit",
        "AfterMainCommitted", "AfterCompletionCommitted", "AfterRestorePublication", "AfterBackoutStarted", "AfterBackoutPublication", "AfterBackedOut", "AfterPreviousAnchorSnapshot"];
    private readonly AcceptanceOfflineIdentity identity;
    private readonly Stream journal;
    private readonly CancellationToken ct;
    private readonly object gate = new();
    private readonly Dictionary<string, AcceptanceOfflineArm> arms = new(StringComparer.Ordinal);
    private AcceptanceOfflineState? state;
    private TaskCompletionSource? release;
    private long sequence;
    private bool closed, failed;
    internal AcceptanceOfflinePause(AcceptanceOfflineIdentity identity, Stream journal, CancellationToken ct)
    {
        if (!Id(identity.StandId) || !Id(identity.RunId) || !Id(identity.RestoreId) || identity.ProcessId <= 0
            || identity.ProcessStartedUtc.Kind != DateTimeKind.Utc || identity.SourceIdentity != Rimlink.Server.Application.Outposts.OutpostProductQualification.SourceIdentity
            || identity.Action is not ("execute" or "resume" or "backout") || !Hash(identity.StandDigest) || !Hash(identity.InvocationSha256) || !Hash(identity.RequestCommitment)
            || !journal.CanSeek || !journal.CanWrite || !journal.CanRead || journal.Length != 0) throw new InvalidDataException("acceptance-offline-origin-invalid");
        this.identity = identity; this.journal = journal; this.ct = ct;
        Emit("Origin", new { });
    }
    internal static void ValidateArm(AcceptanceOfflineArm arm)
    {
        bool file = FileHooks.Contains(arm.Hook);
        if (!Id(arm.ArmId) || arm.Occurrence is < 1 or > 100000 || !file && !Hooks.Contains(arm.Hook)
            || file && (arm.Path is not { Length: > 0 and <= 4096 } path || Path.IsPathFullyQualified(path)
                || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or ".."))
            || !file && arm.Path is not null) throw new InvalidDataException("acceptance-offline-arm-invalid");
    }
    internal void Arm(AcceptanceOfflineArm arm)
    {
        lock (gate)
        {
            RequireActive(); ValidateArm(arm);
            if (arms.TryGetValue(arm.ArmId, out var previous))
            { if (previous != arm) throw new InvalidDataException("acceptance-offline-arm-id-reused"); return; }
            if (arms.Count >= 64 || state?.Status is "Armed" or "Reached") throw new InvalidDataException("acceptance-offline-arm-busy-or-limit");
            Emit("Armed", arm); arms.Add(arm.ArmId, arm); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state = new(arm, "Armed", 0, sequence, null, null, null);
        }
    }
    internal bool Expects(string checkpoint)
    { lock (gate) return state is { Status: "Armed" } s && checkpoint == Key(s.Arm); }
    internal void Reach(string checkpoint, AcceptanceOfflineJournal? observed)
    {
        Task wait;
        lock (gate)
        {
            RequireActive();
            if (state is not { Status: "Armed" } armed || checkpoint != Key(armed.Arm)) return;
            state = armed with { Occurrence = checked(armed.Occurrence + 1) };
            if (state.Occurrence != armed.Arm.Occurrence) return;
            if (observed is null ? armed.Arm.Hook != "AfterPreviousAnchorSnapshot"
                : observed.RestoreId != identity.RestoreId || !Hash(observed.IntentSha256) || !Hash(observed.JournalSha256) || observed.Revision < 0
                    || !Enum.TryParse<Rimlink.Server.Backup.RestorePhase>(observed.Phase, out var phase) || !Enum.IsDefined(phase) || phase.ToString() != observed.Phase
                    || armed.Arm.Path is { } path && !observed.Paths.Contains(path, StringComparer.Ordinal))
                throw new InvalidDataException("acceptance-offline-actual-journal-or-inventory-mismatch");
            var data = new { Arm = armed.Arm, Checkpoint = checkpoint, Occurrence = state.Occurrence,
                Journal = observed is null ? null : new { observed.RestoreId, observed.IntentSha256, observed.Phase, observed.Revision, observed.JournalSha256 } };
            var recorded = Emit("Reached", data);
            state = state with { Status = "Reached", Sequence = sequence, EventSha256 = recorded.Hash, Evidence = recorded.Value };
            wait = release!.Task;
        }
        try { wait.WaitAsync(TimeSpan.FromSeconds(120), ct).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            lock (gate)
            {
                if (state?.Status == "Reached") state = state with { Status = error is TimeoutException ? "TimedOut" : "Cancelled", Error = error.Message };
            }
            throw;
        }
        ct.ThrowIfCancellationRequested();
    }
    internal AcceptanceOfflineState? Observe() { lock (gate) return state; }
    internal void Release(string armId, long expectedSequence, string eventSha256)
    {
        lock (gate)
        {
            RequireActive();
            if (state is not { } current || current.Arm.ArmId != armId || current.Sequence != expectedSequence || current.EventSha256 != eventSha256
                || current.Status is not ("Reached" or "Released")) throw new InvalidDataException("acceptance-offline-release-mismatch");
            if (current.Status == "Released") return;
            Emit("Released", new { ArmId = armId, Sequence = expectedSequence, EventSha256 = eventSha256 });
            state = current with { Status = "Released" }; release!.TrySetResult();
        }
    }
    internal void Cancel(string armId)
    {
        lock (gate)
        {
            RequireActive();
            if (state is not { } current || current.Arm.ArmId != armId) throw new InvalidDataException("acceptance-offline-cancel-mismatch");
            if (current.Status == "Cancelled") return;
            if (current.Status is not ("Armed" or "Reached")) throw new InvalidDataException("acceptance-offline-cancel-stale");
            Emit("Cancelled", new { ArmId = armId }); state = current with { Status = "Cancelled" };
            release!.TrySetCanceled();
        }
    }
    private (string Hash, JsonElement Value) Emit(string kind, object data)
    {
        try
        {
            var value = JsonSerializer.SerializeToElement(new { Version = 1, Identity = identity, Sequence = checked(++sequence), Kind = kind,
                RealUtc = TimeProvider.System.GetUtcNow(), RealTimestamp = TimeProvider.System.GetTimestamp(), RealFrequency = TimeProvider.System.TimestampFrequency, Data = data }, AcceptanceStandFiles.Json);
            byte[] bytes = Encoding.UTF8.GetBytes(value.GetRawText() + "\n");
            if (bytes.Length > 16 * 1024 || journal.Length > 4 * 1024 * 1024 - bytes.Length) throw new IOException("acceptance-offline-evidence-limit");
            long start = journal.Length; journal.Position = start; journal.Write(bytes, 0, bytes.Length);
            if (journal is FileStream file) file.Flush(flushToDisk: true); else journal.Flush();
            journal.Position = start; byte[] actual = new byte[bytes.Length]; journal.ReadExactly(actual);
            if (!actual.AsSpan().SequenceEqual(bytes)) throw new IOException("acceptance-offline-evidence-readback-mismatch");
            return (Convert.ToHexStringLower(SHA256.HashData(bytes)), value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failed = true;
            if (state is { } current) { state = current with { Status = "Failed", Error = error.Message }; if (current.Status == "Reached") release!.TrySetException(error); }
            throw;
        }
    }
    private void RequireActive() { ObjectDisposedException.ThrowIf(closed, this); if (failed) throw new IOException("acceptance-offline-evidence-failed"); }
    private static string Key(AcceptanceOfflineArm arm) => arm.Hook + (arm.Path is null ? "" : ":" + arm.Path);
    private static bool Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    private static bool Hash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public void Dispose()
    {
        lock (gate) { if (closed) return; closed = true; release?.TrySetCanceled(); journal.Dispose(); }
    }
}
