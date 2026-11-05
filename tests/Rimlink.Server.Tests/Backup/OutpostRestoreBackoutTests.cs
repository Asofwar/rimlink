using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreBackoutTests
{
    [Theory]
    [InlineData("AfterOldSetRetained")]
    [InlineData("AfterSwitchEffect:rimlink.db")]
    [InlineData("AfterInstalledVerified")]
    [InlineData("AfterMainPrepared")]
    [InlineData("AfterMainDatabaseCommit")]
    [InlineData("AfterMainCommitted")]
    [InlineData("AfterCompletionCommitted")]
    public async Task Explicit_backout_restores_exact_verified_E_not_backup_C_without_new_boundary(string stop)
    {
        var (f, restore, anchor) = await OutpostRestoreResumeTests.Interrupt(stop);
        using (f)
        {
            var selected = Select(restore);
            var request = selected with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(selected) };
            string root = Path.Combine(anchor, "restores", restore.RestoreId);
            byte[] originalIntent = File.ReadAllBytes(Path.Combine(root, "intent.json"));
            var intent = JsonSerializer.Deserialize<RestoreIntent>(originalIntent, OutpostBackupManifest.Json)!;
            var result = await new OutpostRestoreProcedure().BackoutAsync(request, default);
            Assert.True(result.Outcome == RestoreOutcome.BackedOut, result.Reason);
            Assert.Equal("", result.Reason);
            Assert.Empty(result.Reason);
            Assert.Equal(restore.RestoreId, result.RestoreId);
            Assert.Equal(restore.Inspection.Destination.External!.RestoreBoundary, result.Boundary);
            Assert.Equal(restore.Inspection.Destination.External.Committed.Id, result.HeadId);
            Assert.Equal(originalIntent, File.ReadAllBytes(Path.Combine(root, "intent.json")));
            Assert.Equal(RestorePhase.BackedOut, Journal(restore).Phase);
            foreach (var file in intent.OldFiles)
                Assert.Equal(file.Sha256, SHA256.HashData(File.ReadAllBytes(Path.Combine(restore.Inspection.DataDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar)))));
            f.H.Db.ChangeTracker.Clear();
            Assert.Equal(20, (await f.H.Row()).Stock);
            var observed = await new OutpostRestoreProcedure().InspectAsync(restore.Inspection.ArchivePath, restore.Inspection.DataDirectory, anchor, default);
            Assert.Equal(restore.Inspection.Destination.External, observed.Destination.External);
            var repeated = await new OutpostRestoreProcedure().BackoutAsync(request, default);
            Assert.Equal(result.Outcome, repeated.Outcome);
            Assert.Equal(result.HeadId, repeated.HeadId);
            Assert.Equal(result.HeadDigest, repeated.HeadDigest);
            var opened = await f.H.F.H.H.Authority.OpenAsync(default);
            Assert.Equal(OutpostStartupState.Ready, opened.State);
            Assert.Empty(opened.Reason);
        }
    }

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("changed-commitment")]
    [InlineData("changed-phase")]
    [InlineData("corrupt-retained")]
    [InlineData("published-external")]
    public async Task Backout_refuses_unknown_selection_unusable_old_set_or_external_publication_before_any_live_effect(string damage)
    {
        var (f, restore, anchor) = await OutpostRestoreResumeTests.Interrupt("AfterInstalledVerified");
        using (f)
        {
            var selected = Select(restore);
            if (damage == "changed-commitment") selected = selected with { IntentDigest = new byte[32] };
            if (damage == "changed-phase") selected = selected with { ExpectedPhase = RestorePhase.OldSetRetained.ToString() };
            var request = selected with { ConfirmationDigest = damage == "unconfirmed" ? [] : OutpostRestoreJournalCodec.BackoutDigest(selected) };
            if (damage == "corrupt-retained")
                File.WriteAllBytes(Path.Combine(restore.Inspection.DataDirectory, "restore-work", restore.RestoreId, "retained", "rimlink.db"), [1, 2, 3]);
            if (damage == "published-external")
            {
                var store = f.H.F.H.H.Anchor;
                using var held = await store.AcquireAsync(default);
                var current = store.Read(held)!;
                store.WriteDurable(held, current with { Restore = null, RestoreBoundary = restore.TargetBoundary,
                    Committed = new(Guid.NewGuid().ToString("D"), ByteString.CopyFrom(new byte[32])) }, default);
            }
            string before = await f.Cut();
            byte[] external = File.ReadAllBytes(Path.Combine(anchor, "authority.db"));
            var result = await new OutpostRestoreProcedure().BackoutAsync(request, default);
            Assert.True(result.Outcome is RestoreOutcome.Refused or RestoreOutcome.Maintenance, result.ToString());
            Assert.NotEmpty(result.Reason);
            Assert.Equal(before, await f.Cut());
            Assert.Equal(external, File.ReadAllBytes(Path.Combine(anchor, "authority.db")));
        }
    }

    [Theory]
    [InlineData("AfterInstalledVerified", "AfterBackoutStarted")]
    [InlineData("AfterInstalledVerified", "AfterBackoutPlanned:rimlink.db")]
    [InlineData("AfterInstalledVerified", "AfterBackoutEffect:rimlink.db")]
    [InlineData("AfterInstalledVerified", "AfterBackoutCompleted:rimlink.db")]
    [InlineData("AfterInstalledVerified", "AfterBackoutPublication")]
    [InlineData("AfterInstalledVerified", "AfterBackedOut")]
    [InlineData("AfterMainCommitted", "AfterBackoutStarted")]
    [InlineData("AfterMainCommitted", "AfterBackoutPlanned:rimlink.db")]
    [InlineData("AfterMainCommitted", "AfterBackoutEffect:rimlink.db")]
    [InlineData("AfterMainCommitted", "AfterBackoutCompleted:rimlink.db")]
    [InlineData("AfterMainCommitted", "AfterBackoutPublication")]
    [InlineData("AfterMainCommitted", "AfterBackedOut")]
    [InlineData("AfterCompletionCommitted", "AfterBackoutStarted")]
    [InlineData("AfterCompletionCommitted", "AfterBackoutPlanned:rimlink.db")]
    [InlineData("AfterCompletionCommitted", "AfterBackoutEffect:rimlink.db")]
    [InlineData("AfterCompletionCommitted", "AfterBackoutCompleted:rimlink.db")]
    [InlineData("AfterCompletionCommitted", "AfterBackoutPublication")]
    [InlineData("AfterCompletionCommitted", "AfterBackedOut")]
    public async Task Interrupted_backout_finishes_same_confirmed_action_without_ordinary_resume_choosing_backout(string sourceStop, string stop)
    {
        var (f, restore, anchor) = await OutpostRestoreResumeTests.Interrupt(sourceStop);
        using (f)
        {
            var selected = Select(restore);
            var request = selected with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(selected) };
            var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == stop) throw new StopBackout(); } };
            await Assert.ThrowsAsync<StopBackout>(() => procedure.BackoutAsync(request, default));
            string before = await f.Cut();
            var ordinary = await new OutpostRestoreProcedure().ResumeAsync(restore.Inspection.DataDirectory, anchor, restore.RestoreId, default);
            Assert.True(ordinary.Outcome is RestoreOutcome.Refused or RestoreOutcome.Maintenance, ordinary.ToString());
            Assert.Equal(before, await f.Cut());
            var result = await new OutpostRestoreProcedure().BackoutAsync(request, default);
            Assert.True(result.Outcome == RestoreOutcome.BackedOut, result.Reason);
            Assert.Equal("", result.Reason);
            Assert.Empty(result.Reason);
            Assert.Equal(restore.Inspection.Destination.External!.Committed.Id, result.HeadId);
            Assert.Equal(restore.Inspection.Destination.External.RestoreBoundary, result.Boundary);
            f.H.Db.ChangeTracker.Clear();
            Assert.Equal(20, (await f.H.Row()).Stock);
            Assert.Equal(RestorePhase.BackedOut, Journal(restore).Phase);
            var opened = await f.H.F.H.H.Authority.OpenAsync(default);
            Assert.Equal(OutpostStartupState.Ready, opened.State);
            Assert.Empty(opened.Reason);
        }
    }

    [Theory]
    [InlineData("frozen")]
    [InlineData("main-plan")]
    [InlineData("extra-live")]
    public async Task Changed_unpublished_main_backout_proof_never_changes_live_bytes_or_publishes_E(string damage)
    {
        var (f, restore, anchor) = await OutpostRestoreResumeTests.Interrupt("AfterCompletionCommitted");
        using (f)
        {
            var selection = Select(restore);
            var request = selection with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(selection) };
            var started = new OutpostRestoreProcedure { Checkpoint = point => { if (point == "AfterBackoutStarted") throw new StopBackout(); } };
            await Assert.ThrowsAsync<StopBackout>(() => started.BackoutAsync(request, default));
            if (damage == "frozen")
                File.WriteAllBytes(Path.Combine(restore.Inspection.DataDirectory, "restore-work", restore.RestoreId, "backout-installed", "rimlink.db"), [1, 2, 3]);
            if (damage == "main-plan")
                File.AppendAllText(Path.Combine(anchor, "restores", restore.RestoreId, "main.json"), "\n");
            if (damage == "extra-live")
                File.WriteAllBytes(Path.Combine(restore.Inspection.DataDirectory, "archive", "unexplained.bin"), [1, 2, 3]);
            string before = await f.Cut();
            byte[] external = File.ReadAllBytes(Path.Combine(anchor, "authority.db"));
            var outcome = await new OutpostRestoreProcedure().BackoutAsync(request, default);
            Assert.True(outcome.Outcome is RestoreOutcome.Refused or RestoreOutcome.Maintenance, outcome.Reason);
            Assert.NotEmpty(outcome.Reason);
            Assert.Equal(before, await f.Cut());
            Assert.Equal(external, File.ReadAllBytes(Path.Combine(anchor, "authority.db")));
            Assert.Equal(RestorePhase.BackingOut, Journal(restore).Phase);
        }
    }

    private sealed class StopBackout : Exception;

    private static RestoreBackoutRequest Select(RestoreRequest restore)
    {
        var journal = Journal(restore);
        return new(restore.Inspection.DataDirectory, restore.Inspection.AnchorDirectory, restore.RestoreId,
            journal.IntentDigest.ToArray(), journal.Phase.ToString(), journal.Revision, []);
    }

    private static RestoreJournal Journal(RestoreRequest restore) => JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(
        Path.Combine(restore.Inspection.AnchorDirectory, "restores", restore.RestoreId, "journal.json")), OutpostBackupManifest.Json)!;
}
