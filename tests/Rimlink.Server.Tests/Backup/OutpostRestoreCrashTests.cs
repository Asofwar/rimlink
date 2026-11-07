using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreCrashTests
{
    [Theory]
    [InlineData("AfterJournalCreated", true)]
    [InlineData("AfterExternalFence", false)]
    [InlineData("AfterStageFile:", false)]
    [InlineData("AfterStagedVerified", false)]
    [InlineData("AfterRetainFile:", false)]
    [InlineData("AfterOldSetRetained", false)]
    [InlineData("AfterSwitchPlanned:rimlink.db", false)]
    [InlineData("AfterSwitchEffect:rimlink.db", false)]
    [InlineData("AfterSwitchCompleted:rimlink.db", false)]
    [InlineData("AfterInstalledVerified", false)]
    [InlineData("AfterMainPrepared", false)]
    [InlineData("AfterMainDatabaseCommit", false)]
    [InlineData("AfterMainCommitted", false)]
    [InlineData("AfterCompletionCommitted", false)]
    [InlineData("AfterRestorePublication", true)]
    public async Task Killed_restore_needs_exact_resume_and_commits_selected_cut_once(string point, bool ready)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        string prefix = JsonSerializer.Serialize(await f.H.Db.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync());
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        await f.H.Service.StateAsync(1, new OutpostStateQuery { Scope = f.H.Scope.Clone() }, default);
        Assert.Equal(20, (await f.H.Row()).Stock);
        var request = await OutpostRestoreTests.Request(f, new OutpostRestoreProcedure(), archive);
        var shared = f.H.F.H.H;
        string input = Path.Combine(shared.Data.Backups, "restore-worker-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new WorkerRequest(request.Inspection.ArchivePath,
            request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId,
            request.TargetInstallationId, request.TargetBoundary, request.ConfirmationDigest, request.QualifiedLinuxFileSystem), OutpostBackupManifest.Json));
        f.Host.Dispose();
        var termination = await OutpostFaults.KillAtAsync(shared.Data.Root, point,
            workerTest: "Rimlink.Server.Tests.Backup.OutpostRestoreCrashTests.Owned_restore_crash_worker",
            workerEnvironment: new Dictionary<string, string> { ["RIMLINK_OUTPOST_RESTORE_FAULT_REQUEST"] = input });
        Assert.True(termination.WorkerExitedAfterStreams, termination.ToString());
        var startup = await shared.Authority.OpenAsync(default);
        if (ready && startup != new OutpostStartupResult(OutpostStartupState.Ready)) shared.KeepForDiagnostics = true;
        Assert.True(startup.State == (ready ? OutpostStartupState.Ready : OutpostStartupState.Maintenance),
            $"Unexpected startup: {startup}; termination: {termination}; " +
            $"native OS error: {shared.SqliteFailure.LastFailure}; retained fixture: {shared.Root}; storage exception: {shared.Authority.LastOpenFailure}");
        if (ready) Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), startup);
        else Assert.NotEmpty(startup.Reason!);
        var procedure = new OutpostRestoreProcedure();
        var result = await procedure.ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default);
        if (result.Outcome != RestoreOutcome.Completed || result.Reason != "")
        {
            shared.KeepForDiagnostics = true;
            var roots = new[] { shared.Data.Root, Path.Combine(shared.Data.Root, "restore-work", request.RestoreId, "staged"),
                Path.Combine(shared.Data.Root, "restore-work", request.RestoreId, "retained") };
            var inventory = roots.ToDictionary(root => root, root => Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.Contains(Path.DirectorySeparatorChar + "restore-work" + Path.DirectorySeparatorChar) || root != shared.Data.Root)
                    .Select(path => DiagnosticFile(root, path)).ToArray() : []);
            File.WriteAllText(Path.Combine(shared.Root, "failed-restore-diagnostic.json"), JsonSerializer.Serialize(new
            { result, termination, Native = shared.SqliteFailure.LastFailure, inventory }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(result.Outcome == RestoreOutcome.Completed && result.Reason == "",
            $"Unexpected resume: {result}; termination: {termination}; native OS error: {shared.SqliteFailure.LastFailure}; retained fixture: {shared.Root}");
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
        f.H.Db.ChangeTracker.Clear();
        Assert.Equal(0, (await f.H.Row()).Stock);
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
        var original = await f.H.Db.OutpostTransitions.AsNoTracking().Where(t => t.Kind != "BulkRestore" && t.Kind != "RestoreCompletion")
            .OrderBy(t => t.Id).ToArrayAsync();
        Assert.Equal(prefix, JsonSerializer.Serialize(original));
        var authority = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(request.TargetBoundary, authority.RestoreBoundary);
        Assert.Equal(result.HeadId, authority.HeadId);
        var repeated = await procedure.ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default);
        Assert.Equal(result.HeadId, repeated.HeadId);
        Assert.Equal(result.HeadDigest, repeated.HeadDigest);
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
        Assert.Equal(0, (await f.H.Row()).Stock);
    }

    private static object DiagnosticFile(string root, string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return new { Path = Path.GetRelativePath(root, path), Length = bytes.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                Sidecar = path.EndsWith("-shm", StringComparison.Ordinal) || path.EndsWith("-wal", StringComparison.Ordinal) ? Convert.ToBase64String(bytes) : null };
        }
        catch (IOException error) { return new { Path = Path.GetRelativePath(root, path), Error = error.Message }; }
    }

    // Selected only by the owned parent fixture. A hard kill bypasses every procedure finally/Dispose block.
    [Fact]
    public async Task Owned_restore_crash_worker()
    {
        string? input = Environment.GetEnvironmentVariable("RIMLINK_OUTPOST_RESTORE_FAULT_REQUEST");
        if (input is null) return;
        var selected = JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllBytes(input), OutpostBackupManifest.Json)!;
        string point = Environment.GetEnvironmentVariable("RIMLINK_OUTPOST_FAULT_POINT")!;
        string ready = Environment.GetEnvironmentVariable("RIMLINK_OUTPOST_FAULT_READY")!;
        var procedure = new OutpostRestoreProcedure { Checkpoint = actual =>
        {
            if (actual != point && !(point.EndsWith(':') && actual.StartsWith(point, StringComparison.Ordinal))) return;
            using var worker = System.Diagnostics.Process.GetCurrentProcess();
            File.WriteAllText(ready + ".pending", $"{worker.Id}|{OutpostFaults.StartIdentity(worker)}");
            File.Move(ready + ".pending", ready);
            Thread.Sleep(Timeout.Infinite);
        } };
        var inspection = await procedure.InspectAsync(selected.Archive, selected.Data, selected.Anchor, default);
        var request = new RestoreRequest(selected.Id, RestoreMode.ExistingInstallationRestore, inspection,
            selected.Installation, selected.Boundary, selected.Confirmation, "sqlite-local-v1", selected.Linux);
        var result = await procedure.ExecuteAsync(request, default);
        throw new InvalidOperationException("restore-crash-worker-missed-checkpoint: " + result.Reason);
    }

    private sealed record WorkerRequest(string Archive, string Data, string Anchor, string Id, string Installation,
        string Boundary, byte[] Confirmation, string Linux);
}
