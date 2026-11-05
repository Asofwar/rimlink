using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestorePreviousWitnessTests
{
    [Theory]
    [InlineData("omitted")] [InlineData("unknown")] [InlineData("healthy")] [InlineData("restoring")]
    [InlineData("changed-before-execute")] [InlineData("new-target-is-not-old-authority")]
    public async Task Lost_refuses_unobserved_or_inadmissible_previous_authority_before_creating_any_intent(string kind)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var target = OutpostRestoreModeTests.Target(f); var procedure = new OutpostRestoreProcedure();
        string old = CorruptPrevious(f);
        PreviousAnchorReference? reference = kind switch
        {
            "omitted" => null,
            "unknown" => new(PreviousAnchorReferenceKind.OperatorDeclaredUnknown, null, "Previous location not identified"),
            "healthy" or "restoring" => new(PreviousAnchorReferenceKind.KnownLocation, Path.GetDirectoryName(f.H.F.H.H.Anchor.DatabasePath), null),
            "new-target-is-not-old-authority" => new(PreviousAnchorReferenceKind.KnownLocation, target.Anchor, null),
            _ => new(PreviousAnchorReferenceKind.KnownLocation, old, null)
        };
        if (kind == "restoring")
        {
            using var held = await f.H.F.H.H.Anchor.AcquireAsync(default);
            var known = f.H.F.H.H.Anchor.Read(held)!;
            f.H.F.H.H.Anchor.WriteDurable(held, known with { Restore = new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Google.Protobuf.ByteString.CopyFrom(new byte[32])) }, default);
        }
        var request = await LostRequest(procedure, archive, target.Data, target.Anchor, reference);
        if (kind == "changed-before-execute") File.WriteAllBytes(Path.Combine(old, "authority.db"), [9, 1, 7]);
        byte[] before = File.ReadAllBytes(target.Store.DatabasePath);
        var result = await procedure.ExecuteAsync(request, default);
        Assert.Equal(RestoreOutcome.Refused, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(target.Store.DatabasePath));
        Assert.False(Directory.Exists(Path.Combine(target.Anchor, "restores")));
        Assert.False(File.Exists(Path.Combine(target.Data, "rimlink.db")));
    }

    [Theory]
    [InlineData("previous")] [InlineData("destination")]
    public async Task Changed_fact_after_durable_intent_cannot_publish_the_first_fence_or_be_resumed(string changed)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f); var target = OutpostRestoreModeTests.Target(f);
        string old = CorruptPrevious(f); var procedure = new OutpostRestoreProcedure();
        var request = await LostRequest(procedure, archive, target.Data, target.Anchor, new(PreviousAnchorReferenceKind.KnownLocation, old, null));
        byte[] before = File.ReadAllBytes(target.Store.DatabasePath);
        procedure.Checkpoint = point =>
        {
            if (point != "AfterJournalCreated") return;
            if (changed == "previous") File.WriteAllBytes(Path.Combine(old, "authority.db"), [3, 2, 1]);
            else File.WriteAllBytes(Path.Combine(target.Data, "rimlink.db"), [1, 3, 7]);
        };
        var result = await procedure.ExecuteAsync(request, default);
        Assert.Equal(RestoreOutcome.Refused, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(target.Store.DatabasePath));
        string root = Path.Combine(target.Anchor, "restores", request.RestoreId);
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(Path.Combine(root, "journal.json")), OutpostBackupManifest.Json)!;
        Assert.Equal(RestorePhase.Inspected, journal.Phase);
        procedure.Checkpoint = null;
        Assert.NotEqual(RestoreOutcome.Completed, (await procedure.ResumeAsync(target.Data, target.Anchor, request.RestoreId, default)).Outcome);
        Assert.Equal(before, File.ReadAllBytes(target.Store.DatabasePath));
    }

    [Fact]
    public async Task Confirmation_and_immutable_intent_cover_deep_detached_actual_previous_observation()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f); var target = OutpostRestoreModeTests.Target(f);
        string old = CorruptPrevious(f); var procedure = new OutpostRestoreProcedure();
        var request = await LostRequest(procedure, archive, target.Data, target.Anchor, new(PreviousAnchorReferenceKind.KnownLocation, old, null));
        byte[] selectedBytes = JsonSerializer.SerializeToUtf8Bytes(request.Inspection.PreviousAnchor);
        byte[] confirmed = request.ConfirmationDigest.ToArray();
        var frozen = OutpostRestoreJournalCodec.Freeze(request);
        var changedReference = request with { Inspection = request.Inspection with { PreviousAnchor = request.Inspection.PreviousAnchor! with
            { Reference = request.Inspection.PreviousAnchor.Reference with { Origin = "A different operator selection" } } } };
        Assert.NotEqual(confirmed, OutpostRestoreJournalCodec.SelectionDigest(changedReference));
        request.Inspection.PreviousAnchor!.Files[0].Sha256[0] ^= 1;
        Assert.Equal(selectedBytes, JsonSerializer.SerializeToUtf8Bytes(frozen.PreviousAnchor));
        request = await LostRequest(procedure, archive, target.Data, target.Anchor, new(PreviousAnchorReferenceKind.KnownLocation, old, null));
        procedure.Checkpoint = point => { if (point == "AfterJournalCreated") throw new StopRestore(); };
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        var stored = JsonSerializer.Deserialize<RestoreIntent>(File.ReadAllBytes(Path.Combine(target.Anchor, "restores", request.RestoreId, "intent.json")), OutpostBackupManifest.Json)!;
        var actual = Assert.IsType<PreviousAnchorObservation>(stored.Selection.PreviousAnchor);
        Assert.Equal(selectedBytes, JsonSerializer.SerializeToUtf8Bytes(actual));
        Assert.Equal(PreviousAnchorOutcome.ObservedUnavailable, actual.Outcome);
        Assert.Equal(PreviousAnchorReason.Corrupt, actual.Reason);
        Assert.Equal(SHA256.HashData(new byte[] { 7, 1, 9 }), Assert.Single(actual.Files).Sha256);
    }

    internal static string CorruptPrevious(LifecycleFixture fixture)
    {
        string path = Path.Combine(fixture.H.F.H.H.Root, "previous-external"); Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "authority.db"), [7, 1, 9]);
        return path;
    }

    internal static async Task<RestoreRequest> LostRequest(OutpostRestoreProcedure procedure, string archive, string data, string anchor, PreviousAnchorReference? previous)
    {
        return await OutpostRestoreModeTests.Request(procedure, archive, data, anchor, RestoreMode.LostAnchorRecovery, previous);
    }
    private sealed class StopRestore : Exception;
}
