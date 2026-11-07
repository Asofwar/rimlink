using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminInitializeTests
{
    [Fact]
    public async Task Preview_binds_actual_null_world_and_qualification_without_initializing_or_confirming()
    {
        using var f = new AuthorityHarness(); await OutpostAdminUninitializedInspectionTests.Prepare(f);
        var service = OutpostAdminActivationTests.Service(f);
        var observed = await service.InspectAsync(Actor.Api, default);
        var before = OutpostAdminInspectionTests.Files(f); var startup = f.Feature.Startup;
        string request = Guid.NewGuid().ToString("D");
        var preview = await service.PreviewInitializeAsync(Actor.Api, request, "Initialize provisioned empty installation", default);
        Assert.Empty(preview.Blockers); Assert.NotNull(preview.Command); Assert.False(preview.Command.Confirmed);
        var intent = preview.Command.Intent; Assert.Equal(Actor.Api, intent.Actor); Assert.Equal(request, intent.RequestId);
        Assert.Equal(OutpostAdminActionKind.Initialize, intent.Action); Assert.NotNull(intent.Initialize);
        Assert.Equal(OutpostAdminIntentCodec.ObservationDigest(observed), OutpostAdminIntentCodec.ObservationDigest(intent.Expected));
        Assert.Equal(intent.Expected.Uninitialized!.Schema.ProfileId, intent.Initialize.Schema.ProfileId);
        Assert.Equal(intent.Expected.Uninitialized.Schema.Digest, intent.Initialize.Schema.Digest);
        Assert.Equal(SHA256.HashData(OutpostAdminIntentCodec.Encode(intent)), preview.Command.IntentDigest);
        Assert.NotEqual(Guid.Empty, Guid.ParseExact(intent.Initialize.Installation, "D"));
        Assert.NotEqual(Guid.Empty, Guid.ParseExact(intent.Initialize.Boundary, "D"));
        Assert.Equal(1, intent.Allocation.AuditId); Assert.Empty(intent.Evidence);
        Assert.Equal(before, OutpostAdminInspectionTests.Files(f)); Assert.Equal(startup, f.Feature.Startup);
        Assert.Empty(await f.Db.OutpostAuthority.ToListAsync()); Assert.Empty(await f.Db.OutpostTransitions.ToListAsync());
        Assert.Empty(await f.Db.OutpostAdminCommands.ToListAsync()); Assert.Empty(await f.Db.AdminActions.ToListAsync());
        Assert.False(f.Feature.IsAvailable);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Preview_cannot_reinterpret_committed_or_unprepared_history_as_a_null_store(bool cold)
    {
        using var f = new AuthorityHarness(); await OutpostAdminActivationTests.Prepare(f);
        if (!cold) await OutpostMainObservationTests.Prepare(f, f.MainObservation);
        var before = OutpostAdminInspectionTests.Files(f);
        var preview = await OutpostAdminActivationTests.Service(f).PreviewInitializeAsync(Actor.Api, Guid.NewGuid().ToString("D"), "not an empty installation", default);
        Assert.Null(preview.Command); Assert.Contains("admin-initialize-not-eligible", preview.Blockers);
        Assert.Equal(before, OutpostAdminInspectionTests.Files(f));
    }

    [Theory]
    [InlineData("request")][InlineData("blank")][InlineData("control")][InlineData("oversized")]
    public async Task Preview_rejects_invalid_identity_before_allocating_authority(string problem)
    {
        using var f = new AuthorityHarness(); await OutpostAdminUninitializedInspectionTests.Prepare(f);
        string request = problem == "request" ? "not-a-request-id" : Guid.NewGuid().ToString("D");
        string reason = problem switch { "blank" => " ", "control" => "line\ncontrol", "oversized" => new string('я', 513), _ => "reason" };
        var before = OutpostAdminInspectionTests.Files(f);
        await Assert.ThrowsAsync<InvalidDataException>(() => OutpostAdminActivationTests.Service(f).PreviewInitializeAsync(Actor.Api, request, reason, default));
        Assert.Equal(before, OutpostAdminInspectionTests.Files(f));
    }

    [Fact]
    public async Task Preview_requires_transport_admin_not_a_body_actor_claim()
    {
        using var f = new AuthorityHarness(); await OutpostAdminUninitializedInspectionTests.Prepare(f);
        var before = OutpostAdminInspectionTests.Files(f);
        var error = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => OutpostAdminActivationTests.Service(f).PreviewInitializeAsync(new(1, 1, Role.Player, "player"), Guid.NewGuid().ToString("D"), "reason", default));
        Assert.Equal(global::Grpc.Core.StatusCode.PermissionDenied, error.StatusCode);
        Assert.Equal(before, OutpostAdminInspectionTests.Files(f));
    }
}
