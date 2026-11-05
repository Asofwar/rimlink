using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using ColonyLocks = Rimlink.Server.Application.World.ColonyLocks;
using Rimlink.Server.Backup;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreModeTests
{
    [Fact]
    public async Task Import_binds_a_headless_fence_to_a_new_installation_without_new_genesis()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var prefix = await f.H.Db.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        var target = Target(f);
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var request = await Request(procedure, archive, target.Data, target.Anchor);
        var result = await procedure.ExecuteAsync(request, default);
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
        Assert.Empty(result.Reason);
        Assert.NotEqual(request.Inspection.Manifest.Authority.InstallationId, result.InstallationId);
        await using var db = OutpostRestoreProcedure.OpenRestoreDatabase(target.Data);
        var authority = await db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(request.TargetInstallationId, authority.InstallationId);
        Assert.Equal(request.TargetBoundary, authority.RestoreBoundary);
        Assert.Equal(request.Inspection.Manifest.Authority.WorldId, authority.WorldId);
        Assert.Equal(JsonSerializer.Serialize(prefix), JsonSerializer.Serialize(await db.OutpostTransitions.AsNoTracking()
            .Where(t => t.Kind != "BulkRestore" && t.Kind != "RestoreCompletion").OrderBy(t => t.Id).ToListAsync()));
        Assert.Equal(1, await db.OutpostTransitions.CountAsync(t => t.Kind == "Initialize"));
        Assert.Equal(1, await db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
        var root = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "BulkRestore");
        Assert.Equal(request.Inspection.Manifest.Authority.HeadId, root.OldHeadId);
        Assert.Equal(0, (await db.Outposts.AsNoTracking().SingleAsync()).Stock);
        var feature = new OutpostFeature();
        var service = new Rimlink.Server.Application.Outposts.OutpostAuthority(db, target.Store, new SaveFiles(new Rimlink.Server.Storage.DataDirectory(target.Data)), new ColonyLocks(), feature);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await service.OpenAsync(default));
        using (var held = await target.Store.AcquireAsync(default))
        {
            var known = Assert.IsType<KnownAnchorEnvelope>(target.Store.ReadEnvelope(held));
            Assert.Equal(result.HeadId, known.Record.Committed.Id);
            Assert.Equal(request.TargetInstallationId, known.Record.InstallationId);
        }
        var again = await procedure.ResumeAsync(target.Data, target.Anchor, request.RestoreId, default);
        Assert.Equal(result.HeadId, again.HeadId); Assert.Equal(result.HeadDigest, again.HeadDigest);
    }

    [Theory]
    [InlineData("unknown-file")] [InlineData("unknown-empty-directory")]
    [InlineData("orphan-save")] [InlineData("database-sidecar")]
    [InlineData("same-installation")] [InlineData("foreign-anchor-file")] [InlineData("foreign-anchor-journal")]
    public async Task Import_refuses_unqualified_or_not_new_destination_before_any_fence(string damage)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var target = Target(f);
        if (damage == "unknown-file") File.WriteAllBytes(Path.Combine(target.Data, "unexplained.bin"), [9]);
        if (damage == "unknown-empty-directory") Directory.CreateDirectory(Path.Combine(target.Data, "unexplained"));
        if (damage == "orphan-save")
        { Directory.CreateDirectory(Path.Combine(target.Data, "saves")); File.WriteAllBytes(Path.Combine(target.Data, "saves", "1.rws.zst"), [9]); }
        if (damage == "database-sidecar") File.WriteAllBytes(Path.Combine(target.Data, "rimlink.db-wal"), [9]);
        if (damage == "foreign-anchor-file") File.WriteAllBytes(Path.Combine(target.Anchor, "unexplained.bin"), [9]);
        if (damage == "foreign-anchor-journal") Directory.CreateDirectory(Path.Combine(target.Anchor, "restores", Guid.NewGuid().ToString("D")));
        var procedure = new OutpostRestoreProcedure();
        var request = await Request(procedure, archive, target.Data, target.Anchor);
        if (damage == "same-installation")
        {
            request = request with { TargetInstallationId = request.Inspection.Manifest.Authority.InstallationId };
            request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        }
        byte[] before = File.ReadAllBytes(target.Store.DatabasePath);
        var result = await procedure.ExecuteAsync(request, default);
        Assert.Equal(RestoreOutcome.Refused, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(target.Store.DatabasePath));
        Assert.Equal(damage == "foreign-anchor-journal", Directory.Exists(Path.Combine(target.Anchor, "restores")));
        if (damage == "foreign-anchor-journal") Assert.Single(Directory.EnumerateFileSystemEntries(Path.Combine(target.Anchor, "restores")));
        Assert.False(File.Exists(Path.Combine(target.Data, "rimlink.db")));
    }

    [Theory]
    [InlineData("AfterExternalFence")] [InlineData("AfterMainDatabaseCommit")]
    [InlineData("AfterCompletionCommitted")] [InlineData("AfterRestorePublication")]
    public async Task Lost_anchor_uses_new_healthy_fence_and_never_treats_old_artifacts_as_E(string stop)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var target = Target(f);
        byte[] unknown = [7, 1, 9];
        File.WriteAllBytes(Path.Combine(target.Data, "rimlink.db"), unknown);
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        string previous = OutpostRestorePreviousWitnessTests.CorruptPrevious(f);
        var request = await Request(procedure, archive, target.Data, target.Anchor, RestoreMode.LostAnchorRecovery,
            new(PreviousAnchorReferenceKind.KnownLocation, previous, null));
        procedure.Checkpoint = point => { if (point == stop) throw new StopRestore(); };
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        string journalRoot = Path.Combine(target.Anchor, "restores", request.RestoreId);
        var intent = JsonSerializer.Deserialize<RestoreIntent>(File.ReadAllBytes(Path.Combine(journalRoot, "intent.json")), OutpostBackupManifest.Json)!;
        Assert.Equal(RestorePreviousWitness.Unavailable, intent.PreviousWitness);
        Assert.Equal(previous, intent.Selection.PreviousAnchor!.Reference.Location);
        Assert.Equal(PreviousAnchorOutcome.ObservedUnavailable, intent.Selection.PreviousAnchor.Outcome);
        Assert.Null(intent.Selection.ExternalRecord);
        Assert.NotNull(intent.FreshFenceId);
        if (stop != "AfterRestorePublication")
        {
            using var held = await target.Store.AcquireAsync(default);
            var fence = Assert.IsType<FreshAnchorFence>(target.Store.ReadEnvelope(held));
            Assert.Equal(intent.FreshFenceId, fence.FenceId);
            Assert.Equal(PreviousWitnessKind.Unavailable, fence.PreviousWitness);
        }
        procedure.Checkpoint = null;
        var result = await procedure.ResumeAsync(target.Data, target.Anchor, request.RestoreId, default);
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
        Assert.Equal(unknown, File.ReadAllBytes(Path.Combine(target.Data, "restore-work", request.RestoreId, "retained", "rimlink.db")));
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(Path.Combine(journalRoot, "journal.json")), OutpostBackupManifest.Json)!;
        Assert.False(journal.OldSetUsable); Assert.Equal(RestoreOldSetKind.Unavailable, journal.OldSetKind);
        await using var db = OutpostRestoreProcedure.OpenRestoreDatabase(target.Data);
        Assert.Equal(1, await db.OutpostTransitions.CountAsync(t => t.Kind == "Initialize"));
        Assert.Equal(1, await db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
        Assert.Equal(0, (await db.Outposts.AsNoTracking().SingleAsync()).Stock);
    }

    [Theory]
    [InlineData("AfterInstalledVerified")] [InlineData("AfterMainCommitted")]
    public async Task Import_backout_returns_verified_empty_but_keeps_the_same_nonready_fence(string stop)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f); var target = Target(f);
        var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == stop) throw new StopRestore(); } };
        var request = await Request(procedure, archive, target.Data, target.Anchor);
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        FreshAnchorFence before;
        using (var held = await target.Store.AcquireAsync(default)) before = Assert.IsType<FreshAnchorFence>(target.Store.ReadEnvelope(held));
        string journalPath = Path.Combine(target.Anchor, "restores", request.RestoreId, "journal.json");
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(journalPath), OutpostBackupManifest.Json)!;
        Assert.Equal(RestoreOldSetKind.VerifiedEmpty, journal.OldSetKind);
        var action = new RestoreBackoutRequest(target.Data, target.Anchor, request.RestoreId, journal.IntentDigest,
            journal.Phase.ToString(), journal.Revision, []);
        action = action with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(action) };
        procedure.Checkpoint = null;
        var result = await procedure.BackoutAsync(action, default);
        Assert.True(result.Outcome == RestoreOutcome.BackedOut, result.Reason + "; root: " + string.Join(",", Directory.EnumerateFileSystemEntries(target.Data).Select(Path.GetFileName)));
        Assert.Null(result.HeadId); Assert.Null(result.HeadDigest);
        Assert.False(File.Exists(Path.Combine(target.Data, "rimlink.db")));
        using (var held = await target.Store.AcquireAsync(default))
        {
            Assert.Equal(before with { State = FreshFenceState.BackedOutEmpty }, target.Store.ReadEnvelope(held));
            Assert.False(target.Store.IsPhysicallyUninitialized(held));
        }
        Assert.Equal(RestoreOutcome.BackedOut, (await procedure.BackoutAsync(action, default)).Outcome);
        byte[] fenced = File.ReadAllBytes(target.Store.DatabasePath);
        Assert.NotEqual(RestoreOutcome.Completed, (await procedure.ResumeAsync(target.Data, target.Anchor, request.RestoreId, default)).Outcome);
        Assert.Equal(fenced, File.ReadAllBytes(target.Store.DatabasePath));
    }

    [Fact]
    public async Task Fresh_resume_does_not_publish_a_fence_from_physical_NULL_after_journal_only_crash()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f); var target = Target(f);
        var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == "AfterJournalCreated") throw new StopRestore(); } };
        var request = await Request(procedure, archive, target.Data, target.Anchor);
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        byte[] before = File.ReadAllBytes(target.Store.DatabasePath); procedure.Checkpoint = null;
        Assert.NotEqual(RestoreOutcome.Completed, (await procedure.ResumeAsync(target.Data, target.Anchor, request.RestoreId, default)).Outcome);
        Assert.Equal(before, File.ReadAllBytes(target.Store.DatabasePath));
        using var held = await target.Store.AcquireAsync(default); Assert.Null(target.Store.ReadEnvelope(held));
        Assert.False(File.Exists(Path.Combine(target.Data, "rimlink.db")));
    }

    [Fact]
    public async Task Full_empty_qualification_is_frozen_and_known_empty_directories_survive_import_backout()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f); var target = Target(f);
        string[] originalDirectories = ["archive", "backups", "logs", "saves", "tls"];
        foreach (string name in originalDirectories) Directory.CreateDirectory(Path.Combine(target.Data, name));
        var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == "AfterMainCommitted") throw new StopRestore(); } };
        var request = await Request(procedure, archive, target.Data, target.Anchor);
        var qualification = Assert.IsType<RestoreEmptyQualification>(request.Inspection.Destination.EmptyQualification);
        Assert.Equal(originalDirectories, qualification.KnownEmptyDirectories); Assert.Equal(32, qualification.Digest.Length);
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        string root = Path.Combine(target.Anchor, "restores", request.RestoreId);
        var intent = JsonSerializer.Deserialize<RestoreIntent>(File.ReadAllBytes(Path.Combine(root, "intent.json")), OutpostBackupManifest.Json)!;
        Assert.Equal(JsonSerializer.Serialize(qualification), JsonSerializer.Serialize(intent.Selection.EmptyQualification));
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(Path.Combine(root, "journal.json")), OutpostBackupManifest.Json)!;
        var action = new RestoreBackoutRequest(target.Data, target.Anchor, request.RestoreId, journal.IntentDigest, journal.Phase.ToString(), journal.Revision, []);
        action = action with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(action) }; procedure.Checkpoint = null;
        var result = await procedure.BackoutAsync(action, default);
        Assert.True(result.Outcome == RestoreOutcome.BackedOut, result.Reason);
        foreach (string name in originalDirectories)
        { Assert.True(Directory.Exists(Path.Combine(target.Data, name))); Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(target.Data, name))); }
        Assert.False(File.Exists(Path.Combine(target.Data, "rimlink.db")));
    }

    [Fact]
    public async Task Changed_empty_directory_inventory_after_confirmation_refuses_before_fence()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f); var target = Target(f);
        var procedure = new OutpostRestoreProcedure();
        var request = await Request(procedure, archive, target.Data, target.Anchor);
        Directory.CreateDirectory(Path.Combine(target.Data, "logs"));
        byte[] before = File.ReadAllBytes(target.Store.DatabasePath);
        var result = await procedure.ExecuteAsync(request, default);
        Assert.Equal(RestoreOutcome.Refused, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(target.Store.DatabasePath));
        Assert.False(Directory.Exists(Path.Combine(target.Anchor, "restores")));
        Assert.True(Directory.Exists(Path.Combine(target.Data, "logs")));
    }

    private sealed class StopRestore : Exception;

    internal static (string Data, string Anchor, OutpostAnchorStore Store) Target(LifecycleFixture f)
    {
        string data = Path.Combine(f.H.F.H.H.Root, "import-target");
        string anchor = Path.Combine(f.H.F.H.H.Root, "import-external");
        Directory.CreateDirectory(data); Directory.CreateDirectory(anchor);
        var store = new OutpostAnchorStore(data, Options.Create(new OutpostOptions
        { AnchorDirectory = anchor, StorageContract = "sqlite-local-v1", QualifiedLinuxFileSystem = LocalTestStorage.LinuxQualification(data) }));
        store.Provision();
        return (data, anchor, store);
    }

    internal static async Task<RestoreRequest> Request(OutpostRestoreProcedure procedure, string archive, string data, string anchor,
        RestoreMode mode = RestoreMode.ImportNewHost, PreviousAnchorReference? previousAnchor = null)
    {
        var inspected = await procedure.InspectAsync(archive, data, anchor, default, previousAnchor);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), mode, inspected,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(data));
        return request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
    }
}
