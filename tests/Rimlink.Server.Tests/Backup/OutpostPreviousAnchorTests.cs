using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostPreviousAnchorTests
{
    [Theory]
    [InlineData(8)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    public async Task Unknown_envelope_version_tag_state_mode_or_witness_cannot_be_admitted_as_lost(int offset)
    {
        using var f = await LifecycleFixture.Create(); var target = OutpostRestoreModeTests.Target(f);
        var envelope = new FreshAnchorFence(FreshFenceState.RestoreInProgress, FreshFenceMode.ImportNewHost,
            PreviousWitnessKind.AbsentQualifiedEmpty, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), "world", Guid.NewGuid().ToString("D"), Google.Protobuf.ByteString.CopyFrom(new byte[32]));
        byte[] record = AnchorEnvelopeCodec.Encode(envelope); record[offset] = 255;
        SHA256.HashData(record.AsSpan(0, record.Length - 32)).CopyTo(record.AsSpan(record.Length - 32));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = target.Store.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=PERSIST; UPDATE authority_store SET Record=$record WHERE Id=1;";
            command.Parameters.AddWithValue("$record", record); command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(target.Store.DatabasePath);
        var result = await new OutpostRestoreProcedure().InspectPreviousAnchorAsync(new(PreviousAnchorReferenceKind.KnownLocation, target.Anchor, null), default);
        Assert.Equal(PreviousAnchorOutcome.Inconclusive, result.Outcome); Assert.Equal(PreviousAnchorReason.Unsupported, result.Reason);
        Assert.Equal(PreviousAnchorDigestCoverage.ExactStoreFiles, result.Coverage);
        Assert.Equal(SHA256.HashData(before), Assert.Single(result.Files, x => x.Name == "authority.db").Sha256);
        Assert.Equal(before, File.ReadAllBytes(target.Store.DatabasePath));
    }

    [Fact]
    public async Task Old_location_removed_during_observation_is_inconclusive_not_missing_authority()
    {
        using var f = await LifecycleFixture.Create();
        string old = Path.Combine(f.H.F.H.H.Root, "previous-anchor-vanishes"); Directory.CreateDirectory(old);
        File.WriteAllBytes(Path.Combine(old, "authority.db"), [7, 1, 9]);
        var procedure = new OutpostRestoreProcedure
        { Checkpoint = point => { if (point == "AfterPreviousAnchorSnapshot") Directory.Delete(old, recursive: true); } };
        var result = await procedure.InspectPreviousAnchorAsync(new(PreviousAnchorReferenceKind.KnownLocation, old, null), default);
        Assert.Equal(PreviousAnchorOutcome.Inconclusive, result.Outcome); Assert.Equal(PreviousAnchorReason.ChangedDuringRead, result.Reason);
        Assert.Null(result.Digest); Assert.False(Directory.Exists(old));
    }

    [Fact]
    public async Task Private_snapshot_failure_is_not_evidence_of_denied_old_authority()
    {
        using var f = await LifecycleFixture.Create();
        string old = Path.Combine(f.H.F.H.H.Root, "previous-anchor-private-failure"); Directory.CreateDirectory(old);
        string database = Path.Combine(old, "authority.db"); File.WriteAllBytes(database, [7, 1, 9]);
        var procedure = new OutpostRestoreProcedure
        { Checkpoint = point => { if (point == "AfterPreviousAnchorSnapshot") throw new UnauthorizedAccessException("owned-private-snapshot-failure"); } };
        var result = await procedure.InspectPreviousAnchorAsync(new(PreviousAnchorReferenceKind.KnownLocation, old, null), default);
        Assert.Equal(PreviousAnchorOutcome.Inconclusive, result.Outcome); Assert.Equal(PreviousAnchorReason.Unsupported, result.Reason);
        Assert.Null(result.Digest); Assert.Equal(new byte[] { 7, 1, 9 }, File.ReadAllBytes(database));
        Assert.Single(Directory.EnumerateFileSystemEntries(old));
    }

    [Fact]
    public async Task Known_missing_location_is_observed_unavailable_without_fabricated_empty_or_digest()
    {
        using var f = await LifecycleFixture.Create();
        string missing = Path.Combine(f.H.F.H.H.Root, "previous-anchor-not-present");
        var reference = new PreviousAnchorReference(PreviousAnchorReferenceKind.KnownLocation, missing, null);
        var result = await new OutpostRestoreProcedure().InspectPreviousAnchorAsync(reference, default);
        Assert.Equal(reference, result.Reference); Assert.Equal(PreviousAnchorOutcome.ObservedUnavailable, result.Outcome);
        Assert.Equal(PreviousAnchorReason.Missing, result.Reason); Assert.Equal(PreviousAnchorDigestCoverage.NoBytesAvailable, result.Coverage);
        Assert.Null(result.Digest); Assert.Empty(result.Files); Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task Unknown_location_is_a_declaration_not_an_unavailable_observation()
    {
        var reference = new PreviousAnchorReference(PreviousAnchorReferenceKind.OperatorDeclaredUnknown, null, "Operator cannot identify old storage location");
        var result = await new OutpostRestoreProcedure().InspectPreviousAnchorAsync(reference, default);
        Assert.Equal(reference, result.Reference); Assert.Equal(PreviousAnchorOutcome.UnobservedOperatorDeclaration, result.Outcome);
        Assert.Equal(PreviousAnchorReason.LocationUnknown, result.Reason); Assert.Null(result.Digest); Assert.Empty(result.Files);
    }

    [Fact]
    public async Task Corrupt_old_store_is_bounded_raw_evidence_not_a_trusted_head_or_a_repaired_store()
    {
        using var f = await LifecycleFixture.Create();
        string old = Path.Combine(f.H.F.H.H.Root, "previous-anchor-corrupt"); Directory.CreateDirectory(old);
        byte[] bytes = [7, 1, 9]; string database = Path.Combine(old, "authority.db"); File.WriteAllBytes(database, bytes);
        var result = await new OutpostRestoreProcedure().InspectPreviousAnchorAsync(new(PreviousAnchorReferenceKind.KnownLocation, old, null), default);
        Assert.Equal(PreviousAnchorOutcome.ObservedUnavailable, result.Outcome); Assert.Equal(PreviousAnchorReason.Corrupt, result.Reason);
        Assert.Equal(PreviousAnchorDigestCoverage.ExactStoreFiles, result.Coverage); Assert.Equal(32, Assert.IsType<byte[]>(result.Digest).Length);
        var file = Assert.Single(result.Files); Assert.Equal("authority.db", file.Name); Assert.Equal(3, file.Length); Assert.Equal(SHA256.HashData(bytes), file.Sha256);
        Assert.Equal(bytes, File.ReadAllBytes(database)); Assert.Single(Directory.EnumerateFileSystemEntries(old));
    }

    [Fact]
    public async Task Physical_NULL_old_store_is_observed_unusable_not_qualified_empty_history()
    {
        using var f = await LifecycleFixture.Create(); var target = OutpostRestoreModeTests.Target(f);
        byte[] before = File.ReadAllBytes(target.Store.DatabasePath);
        var result = await new OutpostRestoreProcedure().InspectPreviousAnchorAsync(new(PreviousAnchorReferenceKind.KnownLocation, target.Anchor, null), default);
        Assert.Equal(PreviousAnchorOutcome.ObservedUnavailable, result.Outcome); Assert.Equal(PreviousAnchorReason.Uninitialized, result.Reason);
        Assert.Equal(PreviousAnchorDigestCoverage.ExactStoreFiles, result.Coverage); Assert.NotNull(result.Digest);
        Assert.Equal(before, File.ReadAllBytes(target.Store.DatabasePath));
        using var held = await target.Store.AcquireAsync(default); Assert.Null(target.Store.ReadEnvelope(held));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Real_committed_or_restore_pending_old_store_is_not_lost_and_is_never_mutated(bool restoring)
    {
        using var f = await LifecycleFixture.Create(); var store = f.H.F.H.H.Anchor;
        if (restoring)
        {
            using var held = await store.AcquireAsync(default); var original = store.Read(held)!;
            store.WriteDurable(held, original with { Restore = new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Google.Protobuf.ByteString.CopyFrom(new byte[32])) }, default);
        }
        byte[] before = File.ReadAllBytes(store.DatabasePath);
        string directory = Path.GetDirectoryName(store.DatabasePath)!;
        var result = await new OutpostRestoreProcedure().InspectPreviousAnchorAsync(new(PreviousAnchorReferenceKind.KnownLocation, directory, null), default);
        Assert.Equal(restoring ? PreviousAnchorOutcome.ObservedPendingOrRestore : PreviousAnchorOutcome.ObservedCommitted, result.Outcome);
        Assert.Equal(restoring ? PreviousAnchorReason.OperationInProgress : PreviousAnchorReason.Healthy, result.Reason);
        Assert.Equal(before, File.ReadAllBytes(store.DatabasePath)); Assert.NotNull(result.Digest);
        Assert.DoesNotContain(result.Files, x => x.Name.Contains("restores") || x.Name.Contains("lock"));
    }
}
