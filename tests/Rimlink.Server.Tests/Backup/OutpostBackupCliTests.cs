extern alias tools;
using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using tools::Rimlink.Tools.Commands;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostBackupCliTests
{
    [Fact]
    public async Task Legacy_backup_without_authority_stays_safe_under_running_server_storage_lock()
    {
        using var f = new ServerFixture();
        using var client = f.CreateClient();
        string archive = Path.Combine(f.DataDir, "backups", "legacy-cli.tar.zst");
        using var output = new StringWriter();
        Assert.Equal(0, await Run(BackupCommands.Backup(output), "--data", f.DataDir, "--out", archive));
        var entries = OutpostBackupTests.Read(archive);
        Assert.Contains(BackupArchive.DatabaseFile, entries.Keys);
        Assert.DoesNotContain(OutpostBackupManifest.FileName, entries.Keys);
    }

    [Fact]
    public async Task Stopped_backup_uses_real_protected_capture_not_legacy_shortcut()
    {
        using var f = await LifecycleFixture.Create();
        var shared = f.H.F.H.H;
        f.Host.Dispose();
        string archive = Path.Combine(shared.Root, "cli-cut.tar.zst");
        using var output = new StringWriter();
        int exit = await Run(BackupCommands.Backup(output), "--data", shared.Data.Root, "--out", archive,
            "--anchor", Path.GetDirectoryName(shared.Anchor.DatabasePath)!, "--storage-contract", "sqlite-local-v1",
            "--qualified-linux-filesystem", LocalTestStorage.LinuxQualification(shared.Data.Root));
        Assert.Equal(0, exit);
        var entries = OutpostBackupTests.Read(archive);
        Assert.Contains(OutpostBackupManifest.FileName, entries.Keys);
        Assert.DoesNotContain(entries.Keys, p => p.Contains("authority.db", StringComparison.Ordinal));
        string snapshot = Path.Combine(shared.Root, "cli-cut.db");
        File.WriteAllBytes(snapshot, entries[BackupArchive.DatabaseFile]);
        using var db = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Stock FROM Outposts";
        long stock = (long)command.ExecuteScalar()!;
        Assert.InRange(stock, 1L, 1000L);
        Assert.Equal(stock, (long)(await f.H.Row()).Stock);
        var opened = await shared.Authority.OpenAsync(default);
        Assert.Equal(OutpostStartupState.Ready, opened.State);
        Assert.Empty(opened.Reason);
    }

    [Fact]
    public async Task Protected_backup_refuses_running_server_before_output_or_settlement()
    {
        using var f = await LifecycleFixture.Create();
        var shared = f.H.F.H.H;
        string archive = Path.Combine(shared.Root, "running-cli.tar.zst");
        string before = await f.Cut();
        using var output = new StringWriter();
        int exit = await Run(BackupCommands.Backup(output), "--data", shared.Data.Root, "--out", archive,
            "--anchor", Path.GetDirectoryName(shared.Anchor.DatabasePath)!, "--storage-contract", "sqlite-local-v1",
            "--qualified-linux-filesystem", LocalTestStorage.LinuxQualification(shared.Data.Root));
        Assert.NotEqual(0, exit);
        Assert.False(File.Exists(archive));
        Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Inspect_emits_frozen_selection_without_creating_destination_or_anchor()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        string data = Path.Combine(f.H.F.H.H.Root, "cli-uncreated-data");
        string anchor = Path.Combine(f.H.F.H.H.Root, "cli-uncreated-anchor");
        using var output = new StringWriter();
        int exit = await Run(BackupCommands.Restore(output), "inspect", "--data", data, "--anchor", anchor, "--from", archive);
        Assert.Equal(0, exit);
        var inspected = JsonSerializer.Deserialize<RestoreInspection>(output.ToString(), OutpostBackupManifest.Json)!;
        Assert.Equal(f.H.Scope.RestoreBoundary, inspected.Manifest.Authority.RestoreBoundary);
        Assert.Equal(RestoreAnchorState.Unavailable, inspected.Destination.AnchorState);
        Assert.False(Directory.Exists(data));
        Assert.False(Directory.Exists(anchor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspect_records_selected_previous_authority_without_claiming_it_is_lost(bool healthy)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var shared = f.H.F.H.H;
        string previous = healthy ? Path.GetDirectoryName(shared.Anchor.DatabasePath)! : Path.Combine(shared.Root, "cli-missing-previous");
        string data = Path.Combine(shared.Root, "cli-fresh-data");
        string anchor = Path.Combine(shared.Root, "cli-fresh-anchor");
        string before = await f.Cut();
        using var output = new StringWriter();
        Assert.Equal(0, await Run(BackupCommands.Restore(output), "inspect", "--data", data, "--anchor", anchor, "--from", archive,
            "--previous-anchor", previous));
        var inspected = JsonSerializer.Deserialize<RestoreInspection>(output.ToString(), OutpostBackupManifest.Json)!;
        var observation = Assert.IsType<PreviousAnchorObservation>(inspected.PreviousAnchor);
        Assert.Equal(PreviousAnchorReferenceKind.KnownLocation, observation.Reference.Kind);
        Assert.Equal(Path.GetFullPath(previous), observation.Reference.Location);
        Assert.Equal(healthy ? PreviousAnchorOutcome.ObservedCommitted : PreviousAnchorOutcome.ObservedUnavailable, observation.Outcome);
        Assert.Equal(healthy ? PreviousAnchorReason.Healthy : PreviousAnchorReason.Missing, observation.Reason);
        Assert.Equal(before, await f.Cut());
        Assert.False(Directory.Exists(data)); Assert.False(Directory.Exists(anchor));
        if (!healthy) Assert.False(Directory.Exists(previous));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_uses_exact_external_confirmation_and_same_typed_procedure(bool changedConfirmation)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        f.Host.Dispose();
        var request = await OutpostRestoreTests.Request(f, new OutpostRestoreProcedure(), archive);
        string path = Path.Combine(f.H.F.H.H.Root, "cli-request.json");
        // The action confirmation is separate: an embedded digest is deliberately not authority.
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(request with { ConfirmationDigest = [] }, OutpostBackupManifest.Json));
        using (var preview = new StringWriter())
        {
            int digestExit = await Run(BackupCommands.Restore(preview), "digest", "--request", path);
            Assert.Equal(0, digestExit);
            Assert.Equal(Convert.ToHexString(request.ConfirmationDigest), preview.ToString().Trim());
            Assert.NotEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), preview.ToString().Trim());
        }
        string before = await f.Cut();
        using var output = new StringWriter();
        int exit = await Run(BackupCommands.Restore(output), "execute", "--request", path, "--confirm",
            Convert.ToHexString(changedConfirmation ? new byte[32] : request.ConfirmationDigest));
        var result = JsonSerializer.Deserialize<RestoreResult>(output.ToString(), OutpostBackupManifest.Json)!;
        if (changedConfirmation)
        {
            Assert.NotEqual(0, exit);
            Assert.Equal(RestoreOutcome.Refused, result.Outcome);
            Assert.Equal(before, await f.Cut());
        }
        else
        {
            Assert.Equal(0, exit);
            Assert.Equal(RestoreOutcome.Completed, result.Outcome);
            Assert.Equal(request.TargetBoundary, result.Boundary);
            f.H.Db.ChangeTracker.Clear();
            Assert.Single(await f.H.Db.OutpostLifecycleOperations.Where(x => x.Kind == "BulkRestore").ToArrayAsync());
            Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_or_confirmed_backout_reuses_exact_retained_operation(bool backout)
    {
        var (f, request, anchor) = await OutpostRestoreResumeTests.Interrupt("AfterInstalledVerified");
        using (f)
        using (var output = new StringWriter())
        {
            int exit;
            if (backout)
            {
                var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(Path.Combine(anchor, "restores", request.RestoreId, "journal.json")), OutpostBackupManifest.Json)!;
                string before = await f.Cut();
                using var inspection = new StringWriter();
                Assert.Equal(0, await Run(BackupCommands.Restore(inspection), "backout-inspect", "--data", request.Inspection.DataDirectory,
                    "--anchor", anchor, "--restore-id", request.RestoreId));
                Assert.Equal(before, await f.Cut());
                var action = JsonSerializer.Deserialize<RestoreBackoutRequest>(inspection.ToString(), OutpostBackupManifest.Json)!;
                Assert.Equal("InstalledVerified", action.ExpectedPhase);
                Assert.Equal(journal.Revision, action.ExpectedRevision);
                Assert.Equal(SHA256.HashData(File.ReadAllBytes(Path.Combine(anchor, "restores", request.RestoreId, "intent.json"))), action.IntentDigest);
                Assert.Empty(action.ConfirmationDigest);
                string path = Path.Combine(f.H.F.H.H.Root, "cli-backout.json");
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(action, OutpostBackupManifest.Json));
                using (var preview = new StringWriter())
                {
                    Assert.Equal(0, await Run(BackupCommands.Restore(preview), "backout-digest", "--request", path));
                    Assert.Equal(Convert.ToHexString(OutpostRestoreJournalCodec.BackoutDigest(action)), preview.ToString().Trim());
                }
                exit = await Run(BackupCommands.Restore(output), "backout", "--request", path, "--confirm",
                    Convert.ToHexString(OutpostRestoreJournalCodec.BackoutDigest(action)));
            }
            else exit = await Run(BackupCommands.Restore(output), "resume", "--data", request.Inspection.DataDirectory,
                "--anchor", anchor, "--restore-id", request.RestoreId);
            Assert.Equal(0, exit);
            var result = JsonSerializer.Deserialize<RestoreResult>(output.ToString(), OutpostBackupManifest.Json)!;
            Assert.Equal(backout ? RestoreOutcome.BackedOut : RestoreOutcome.Completed, result.Outcome);
            Assert.Equal(backout ? request.Inspection.Destination.External!.RestoreBoundary : request.TargetBoundary, result.Boundary);
            Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
        }
    }

    private static Task<int> Run(Command command, params string[] args)
    {
        var root = new RootCommand { command };
        return root.Parse(new[] { command.Name }.Concat(args).ToArray()).InvokeAsync();
    }
}
