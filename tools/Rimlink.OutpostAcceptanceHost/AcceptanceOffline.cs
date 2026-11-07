using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed record AcceptanceOfflineInvocation(int Version, string Action, string? RequestFile = null, string? ConfirmationDigest = null,
    string? RestoreId = null, AcceptanceOfflineArm? Arm = null);

internal sealed class AcceptanceOffline : IAcceptanceControlTarget, IDisposable
{
    private readonly AcceptanceStand stand;
    private readonly AcceptanceComposition composition;
    private readonly AcceptanceOfflineInvocation invocation;
    private readonly string descriptorHash, invocationPath, invocationHash, evidenceRoot, evidenceIdentity;
    private readonly string? requestHash;
    private readonly RestoreRequest? execute;
    private readonly RestoreBackoutRequest? backout;
    private readonly string restoreId, requestCommitment;
    private readonly CancellationTokenSource workerStop = new();
    private readonly AcceptanceOfflinePause pause;
    private readonly Dictionary<string, (string Hash, AcceptanceControlReply Reply)> commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> heldFiles = new(AcceptancePaths.Comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private string? intentHash;
    private long lastControl = Stopwatch.GetTimestamp();
    internal AcceptanceControlBinding Binding { get; }
    internal string BindingPath { get; }

    private AcceptanceOffline(AcceptanceStand stand, AcceptanceComposition composition, string invocationPath,
        AcceptanceOfflineInvocation invocation, RestoreRequest? execute, RestoreBackoutRequest? backout)
    {
        this.stand = stand; this.composition = composition; this.invocationPath = invocationPath; this.invocation = invocation;
        this.execute = execute; this.backout = backout;
        descriptorHash = AcceptanceStandFiles.HashFile(Path.Combine(stand.Root, "stand.json"));
        invocationHash = AcceptanceStandFiles.HashFile(invocationPath);
        requestHash = invocation.RequestFile is null ? null : AcceptanceStandFiles.HashFile(invocation.RequestFile);
        evidenceRoot = OwnedEvidence(stand); evidenceIdentity = AcceptancePaths.DirectoryIdentity(evidenceRoot);
        restoreId = execute?.RestoreId ?? backout?.RestoreId ?? invocation.RestoreId!;
        if (!Guid.TryParseExact(restoreId, "D", out var id) || id == Guid.Empty) throw new InvalidDataException("acceptance-offline-restore-id-invalid");
        if (invocation.Action != "execute")
        {
            var loaded = OutpostRestoreProcedure.LoadJournal(stand.DataDirectory, stand.AnchorDirectory, restoreId);
            CheckSelection(stand, loaded.Intent.Selection);
            intentHash = Sha(loaded.IntentBytes);
            if (backout is not null && !backout.IntentDigest.AsSpan().SequenceEqual(SHA256.HashData(loaded.IntentBytes)))
                throw new InvalidDataException("acceptance-offline-backout-intent-mismatch");
        }
        requestCommitment = execute is not null ? Convert.ToHexStringLower(OutpostRestoreJournalCodec.SelectionDigest(execute))
            : backout is not null ? Convert.ToHexStringLower(OutpostRestoreJournalCodec.BackoutDigest(backout)) : intentHash!;
        string run = Guid.NewGuid().ToString("D");
        Binding = new(1, stand.StandId, run, OutpostProductQualification.SourceIdentity, "rlo-" + Guid.Parse(run).ToString("N"),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        BindingPath = Path.Combine(evidenceRoot, "offline-" + run + ".control.json");
        var stream = new FileStream(Path.Combine(evidenceRoot, "offline-" + run + ".events.jsonl"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            pause = new(new(stand.StandId, run, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime(), Binding.SourceIdentity,
                invocation.Action, restoreId, descriptorHash, invocationHash, requestCommitment), stream, workerStop.Token);
            if (invocation.Arm is { } arm) pause.Arm(arm);
            using var file = new FileStream(BindingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, Binding, AcceptanceStandFiles.Json); file.Flush(flushToDisk: true);
        }
        catch { stream.Dispose(); throw; }
    }

    internal static async Task<int> RunAsync(AcceptanceStand stand, string invocationPath)
    {
        invocationPath = Path.GetFullPath(invocationPath); RequireEvidenceFile(stand, invocationPath);
        string frozenInput = AcceptanceStandFiles.HashFile(invocationPath);
        var input = AcceptanceStandFiles.Read<AcceptanceOfflineInvocation>(invocationPath); ValidateInvocation(input);
        using var composition = AcceptanceComposition.Open(stand); RequireQualified(composition);
        using (StoppedProbe(stand)) { }
        RestoreRequest? execute = null; RestoreBackoutRequest? backout = null;
        string? frozenRequest = null;
        if (input.RequestFile is { } request)
        {
            RequireEvidenceFile(stand, request); frozenRequest = AcceptanceStandFiles.HashFile(request);
            if (input.Action == "execute")
            {
                execute = await OutpostRestoreRequestJson.ReadAsync<RestoreRequest>(request, default);
                execute = execute with { ConfirmationDigest = Convert.FromHexString(input.ConfirmationDigest!) };
                CheckSelection(stand, OutpostRestoreJournalCodec.Freeze(execute));
            }
            else
            {
                backout = await OutpostRestoreRequestJson.ReadAsync<RestoreBackoutRequest>(request, default);
                backout = backout with { ConfirmationDigest = Convert.FromHexString(input.ConfirmationDigest!) };
                _ = OutpostRestoreJournalCodec.BackoutDigest(backout);
                if (backout.DataDirectory != stand.DataDirectory || backout.AnchorDirectory != stand.AnchorDirectory)
                    throw new InvalidDataException("acceptance-offline-backout-outside-stand");
            }
            if (AcceptanceStandFiles.HashFile(request) != frozenRequest) throw new InvalidDataException("acceptance-offline-request-changed-during-read");
        }
        if (AcceptanceStandFiles.HashFile(invocationPath) != frozenInput) throw new InvalidDataException("acceptance-offline-invocation-changed-during-read");
        using var runtime = new AcceptanceOffline(stand, composition, invocationPath, input, execute, backout);
        if (runtime.invocationHash != frozenInput || runtime.requestHash != frozenRequest) throw new InvalidDataException("acceptance-offline-input-changed-before-worker");
        return await runtime.RunWorkerAsync();
    }

    internal static void ValidateInvocation(AcceptanceOfflineInvocation input)
    {
        if (input.Version != 1 || input.Action is not ("execute" or "resume" or "backout")) throw new InvalidDataException("acceptance-offline-action-invalid");
        if (input.Action == "resume" ? input.RequestFile is not null || input.ConfirmationDigest is not null || input.RestoreId is null
            : input.RequestFile is null || input.RestoreId is not null || input.ConfirmationDigest is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit))
            throw new InvalidDataException("acceptance-offline-invocation-shape-invalid");
        if (input.RestoreId is { } restore && (!Guid.TryParseExact(restore, "D", out var id) || id == Guid.Empty))
            throw new InvalidDataException("acceptance-offline-restore-id-invalid");
        if (input.Arm is { } arm) AcceptanceOfflinePause.ValidateArm(arm);
    }

    private async Task<int> RunWorkerAsync()
    {
        using var listenerStop = new CancellationTokenSource();
        var listener = new AcceptanceControlPipe(Binding, this).ServeAsync(listenerStop.Token);
        var lease = WatchControllerAsync(listenerStop.Token);
        Console.WriteLine("Private offline control binding: " + BindingPath);
        var worker = Task.Run(async () =>
        {
            VerifyInputs();
            var procedure = new OutpostRestoreProcedure { Checkpoint = Checkpoint };
            return invocation.Action switch
            {
                "execute" => await procedure.ExecuteAsync(execute!, workerStop.Token),
                "resume" => await procedure.ResumeAsync(stand.DataDirectory, stand.AnchorDirectory, restoreId, workerStop.Token),
                "backout" => await procedure.BackoutAsync(backout!, workerStop.Token),
                _ => throw new InvalidDataException("acceptance-offline-action-invalid")
            };
        });
        try
        {
            if (await Task.WhenAny(worker, listener, lease) != worker) workerStop.Cancel();
            var result = await worker;
            Console.WriteLine(OutpostRestoreRequestJson.Serialize(new { isolatedAcceptance = true, noEffectAssertion = false, result }));
            return result.Outcome is RestoreOutcome.Completed or RestoreOutcome.BackedOut ? 0 : 2;
        }
        finally
        {
            workerStop.Cancel(); listenerStop.Cancel();
            try { await listener; } catch (OperationCanceledException) when (listenerStop.IsCancellationRequested) { }
            try { await lease; } catch (OperationCanceledException) when (listenerStop.IsCancellationRequested) { }
        }
    }

    private async Task WatchControllerAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(1000, ct);
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastControl)) > TimeSpan.FromSeconds(30))
            { workerStop.Cancel(); return; }
        }
    }
    public ValueTask<AcceptanceControlReply> HandleAsync(AcceptanceControlCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); VerifyInputs(); Interlocked.Exchange(ref lastControl, Stopwatch.GetTimestamp());
        if (command.Action == "offline-observe") return ValueTask.FromResult(Reply());
        var control = command.Offline ?? throw new InvalidDataException("acceptance-offline-control-body-required");
        if (!Guid.TryParseExact(control.CommandId, "D", out var id) || id == Guid.Empty)
            throw new InvalidDataException("acceptance-offline-control-id-or-limit");
        string hash = Sha(JsonSerializer.SerializeToUtf8Bytes(command, AcceptanceStandFiles.Json));
        if (commands.TryGetValue(control.CommandId, out var previous))
            return ValueTask.FromResult(previous.Hash == hash ? previous.Reply : throw new InvalidDataException("acceptance-offline-control-id-reused"));
        if (commands.Count >= 256) throw new InvalidDataException("acceptance-offline-control-limit");
        switch (command.Action)
        {
            case "offline-arm": pause.Arm(control.Arm ?? throw new InvalidDataException("acceptance-offline-arm-required")); break;
            case "offline-release":
                lock (heldFiles) foreach (var (path, expected) in heldFiles)
                    if (AcceptanceStandFiles.HashFile(path) != expected) throw new InvalidDataException("acceptance-offline-held-journal-changed");
                pause.Release(control.ArmId ?? "", control.Sequence, control.EventSha256 ?? ""); break;
            case "offline-cancel": pause.Cancel(control.ArmId ?? ""); workerStop.Cancel(); break;
            default: throw new InvalidDataException("acceptance-offline-control-action-invalid");
        }
        var reply = Reply(); commands.Add(control.CommandId, (hash, reply)); return ValueTask.FromResult(reply);
    }
    private AcceptanceControlReply Reply() => new(true, "offline-observed", JsonSerializer.SerializeToElement(new
    { Binding.StandId, Binding.RunId, Binding.SourceIdentity, ProcessId = Environment.ProcessId, Action = invocation.Action, RestoreId = restoreId,
        RequestCommitment = requestCommitment, Pause = pause.Observe(), Cancelled = workerStop.IsCancellationRequested }, AcceptanceStandFiles.Json));

    private void Checkpoint(string point)
    {
        if (!pause.Expects(point)) return;
        VerifyInputs();
        string root = Path.Combine(stand.AnchorDirectory, "restores", restoreId);
        AcceptanceOfflineJournal? observed = null;
        lock (heldFiles)
        {
            heldFiles.Clear();
            if (point != "AfterPreviousAnchorSnapshot" || Directory.Exists(root))
            {
                var loaded = OutpostRestoreProcedure.LoadJournal(stand.DataDirectory, stand.AnchorDirectory, restoreId);
                CheckSelection(stand, loaded.Intent.Selection);
                string actualIntent = Sha(loaded.IntentBytes);
                if (intentHash is not null && intentHash != actualIntent || execute is not null
                    && !loaded.Intent.SelectionDigest.AsSpan().SequenceEqual(OutpostRestoreJournalCodec.SelectionDigest(execute)))
                    throw new InvalidDataException("acceptance-offline-original-intent-changed");
                intentHash ??= actualIntent;
                heldFiles.Add(Path.Combine(root, "intent.json"), actualIntent);
                string journal = Path.Combine(root, "journal.json"); string journalHash = AcceptanceStandFiles.HashFile(journal);
                heldFiles.Add(journal, journalHash);
                var paths = loaded.Intent.OldFiles.Concat(loaded.Intent.NewFiles).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
                paths.Add(OutpostBackupManifest.FileName);
                string installedPath = Path.Combine(root, "backout-installed.json");
                if (point.StartsWith("AfterBackout", StringComparison.Ordinal) && File.Exists(installedPath))
                {
                    var installed = AcceptanceStandFiles.Read<OutpostRestoreProcedure.BackoutInstalled>(installedPath);
                    string main = Path.Combine(root, "main.json");
                    if (installed.Version != 1 || !installed.MainDigest.AsSpan().SequenceEqual(Convert.FromHexString(AcceptanceStandFiles.HashFile(main))))
                        throw new InvalidDataException("acceptance-offline-backout-installed-binding-invalid");
                    heldFiles.Add(installedPath, AcceptanceStandFiles.HashFile(installedPath)); heldFiles.Add(main, AcceptanceStandFiles.HashFile(main));
                    foreach (var file in installed.Files) paths.Add(file.Path);
                }
                observed = new(restoreId, actualIntent, loaded.Journal.Phase.ToString(), loaded.Journal.Revision, journalHash, paths.ToArray());
            }
        }
        pause.Reach(point, observed);
        VerifyInputs();
        lock (heldFiles) foreach (var (path, hash) in heldFiles)
            if (AcceptanceStandFiles.HashFile(path) != hash) throw new InvalidDataException("acceptance-offline-journal-changed-before-continuation");
    }

    private void VerifyInputs()
    {
        RequireQualified(composition);
        if (AcceptanceStandFiles.HashFile(Path.Combine(stand.Root, "stand.json")) != descriptorHash
            || AcceptanceStandFiles.HashFile(invocationPath) != invocationHash || invocation.RequestFile is { } request && AcceptanceStandFiles.HashFile(request) != requestHash
            || AcceptancePaths.DirectoryIdentity(OwnedEvidence(stand)) != evidenceIdentity)
            throw new InvalidDataException("acceptance-offline-owned-input-changed");
        if (intentHash is not null && AcceptanceStandFiles.HashFile(Path.Combine(stand.AnchorDirectory, "restores", restoreId, "intent.json")) != intentHash)
            throw new InvalidDataException("acceptance-offline-original-journal-changed");
    }
    private static void RequireQualified(AcceptanceComposition composition)
    {
        if (composition.Observe() is not { Mode: OutpostRuntimeMode.IsolatedAcceptance, AutomatedPrerequisitesVerified: true, IsolationVerified: true })
            throw new InvalidDataException("acceptance-offline-current-owned-qualification-required");
    }
    private static FileStream StoppedProbe(AcceptanceStand stand) => new(Path.Combine(stand.DataDirectory, BackupArchive.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static string OwnedEvidence(AcceptanceStand stand)
    {
        string root = AcceptancePaths.Directory(Path.Combine(stand.Root, "evidence")); string identity = AcceptancePaths.DirectoryIdentity(root);
        if (new[] { stand.DataDirectory, stand.AnchorDirectory, stand.GameRoot, stand.HostSaveData, stand.GuestSaveData }
            .Any(p => AcceptancePaths.DirectoryIdentity(p) == identity)) throw new InvalidDataException("acceptance-offline-evidence-overlap");
        return root;
    }
    private static void RequireEvidenceFile(AcceptanceStand stand, string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path || !AcceptancePaths.Within(path, OwnedEvidence(stand)))
            throw new InvalidDataException("acceptance-offline-input-outside-evidence");
        AcceptancePaths.File(path);
    }
    private static void CheckSelection(AcceptanceStand stand, RestoreSelection selected)
    {
        OutpostRestoreJournalCodec.ValidateSelection(selected);
        if (selected.DataDirectory != stand.DataDirectory || selected.AnchorDirectory != stand.AnchorDirectory
            || selected.StorageContract != "sqlite-local-v1" || selected.QualifiedLinuxFileSystem != stand.QualifiedLinuxFileSystem
            || !AcceptancePaths.Within(selected.ArchivePath, stand.BackupDirectory) && !AcceptancePaths.Within(selected.ArchivePath, OwnedEvidence(stand)))
            throw new InvalidDataException("acceptance-offline-selection-outside-stand");
        AcceptancePaths.File(selected.ArchivePath);
        if (selected.PreviousAnchor?.Reference.Location is { } previous)
        {
            if (!Path.IsPathFullyQualified(previous) || !AcceptancePaths.Within(Path.GetFullPath(previous), stand.Root))
                throw new InvalidDataException("acceptance-offline-previous-anchor-outside-stand");
            for (string? path = previous; path is not null; path = Path.GetDirectoryName(path))
                if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("acceptance-offline-previous-anchor-aliased");
        }
    }
    internal static async Task<int> InspectAsync(AcceptanceStand stand, string value, bool backout)
    {
        using var composition = AcceptanceComposition.Open(stand); RequireQualified(composition); using var stopped = StoppedProbe(stand);
        var procedure = new OutpostRestoreProcedure();
        if (backout) Console.WriteLine(OutpostRestoreRequestJson.Serialize(procedure.InspectBackout(stand.DataDirectory, stand.AnchorDirectory, value)));
        else
        {
            string archive = Path.GetFullPath(value);
            if (!AcceptancePaths.Within(archive, stand.BackupDirectory) && !AcceptancePaths.Within(archive, OwnedEvidence(stand))) throw new InvalidDataException("acceptance-offline-archive-outside-stand");
            AcceptancePaths.File(archive);
            Console.WriteLine(OutpostRestoreRequestJson.Serialize(await procedure.InspectAsync(archive, stand.DataDirectory, stand.AnchorDirectory, default)));
        }
        return 0;
    }
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() { workerStop.Cancel(); pause.Dispose(); workerStop.Dispose(); }
}
