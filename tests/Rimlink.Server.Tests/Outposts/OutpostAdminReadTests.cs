using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminReadTests
{
    [Fact]
    public async Task Read_view_is_bounded_readonly_and_exposes_only_verified_summaries_not_payloads()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        // This is a direct-service file-immutability assertion. Stop only this fixture-owned host so its PID lock is readable.
        f.Host.Dispose();
        string before = await f.Cut(), files = OutpostAdminInspectionTests.Files(h);
        var view = await OutpostAdminActivationTests.Service(h).InspectAsync(new(0, 0, Role.Moderator, "api:read"), new(), default);
        Assert.Equal(OutpostAuthorityObservationKind.Committed, view.Observation.Kind);
        Assert.Equal(OutpostAdminIntentCodec.ObservationDigest(view.Observation), view.ObservationDigest);
        Assert.True(view.HasHistory); Assert.False(view.AdvertiseCapability); Assert.NotEmpty(view.Blockers);
        Assert.False(view.HasMore); Assert.Null(view.NextCursor); Assert.InRange(view.Rows.Length, 1, 50);
        Assert.Contains(view.Rows, r => r.Kind == "Operation"); Assert.Contains(view.Rows, r => r.Kind == "Custody");
        Assert.Contains(view.Rows, r => r.Kind == "Manifest");
        Assert.All(view.Rows.SelectMany(r => r.Evidence), e => Assert.Equal(32, e.Digest!.Length));
        string json = JsonSerializer.Serialize(view);
        using var document = JsonDocument.Parse(json);
        AssertNoPayloadFields(document.RootElement);
        var original = OutpostPrepareRequest.Parser.ParseFrom(await h.Db.OutpostOperations.AsNoTracking()
            .Where(o => o.Kind == "Found" || o.Kind == "Assign").Select(o => o.Intent).FirstAsync());
        Assert.NotEmpty(original.Workers);
        foreach (var worker in original.Workers)
            Assert.DoesNotContain(Convert.ToBase64String(worker.Pawn.ScribeXmlZstd.Span), json, StringComparison.Ordinal);
        Assert.Equal(before, await f.Cut()); Assert.Equal(files, OutpostAdminInspectionTests.Files(h));
    }

    [Fact]
    public void Payload_field_check_does_not_misclassify_random_base64_sha_text_as_xml()
    {
        using var document = JsonDocument.Parse("{\"Digest\":\"dzcHEj6bVLSGx0cBXJ0BxMLnCyazRcU7/QLhwvg=\"}");
        AssertNoPayloadFields(document.RootElement);
    }

    private static void AssertNoPayloadFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Assert.DoesNotContain("xml", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("blob", property.Name, StringComparison.OrdinalIgnoreCase);
                AssertNoPayloadFields(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) AssertNoPayloadFields(child);
    }

    [Fact]
    public async Task Pages_pin_the_whole_observation_and_reject_a_changed_head_or_filter()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        var service = OutpostAdminActivationTests.Service(h); string before = await f.Cut();
        var query = new OutpostAdminQuery(Limit: 1);
        var first = await service.InspectAsync(Actor.Api, query, default);
        Assert.Single(first.Rows); Assert.True(first.HasMore); Assert.NotNull(first.NextCursor);
        var second = await service.InspectAsync(Actor.Api, query with { Cursor = first.NextCursor }, default);
        Assert.Single(second.Rows); Assert.NotEqual(first.Rows[0].Kind + first.Rows[0].Id, second.Rows[0].Kind + second.Rows[0].Id);
        Assert.Equal(first.ObservationDigest, second.ObservationDigest); Assert.Equal(before, await f.Cut());
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => service.InspectAsync(Actor.Api,
            query with { PlayerId = 1, Cursor = first.NextCursor }, default));
        await service.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(h), default);
        before = await f.Cut();
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => service.InspectAsync(Actor.Api,
            query with { Cursor = first.NextCursor }, default));
        Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Continuations_cover_the_same_complete_cut_without_duplicates_or_payloads()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        var service = OutpostAdminActivationTests.Service(h);
        var complete = await service.InspectAsync(Actor.Api, new(Limit: 100), default);
        var rows = new List<OutpostAdminSummary>(); string? cursor = null;
        do
        {
            var page = await service.InspectAsync(Actor.Api, new(Limit: 1, Cursor: cursor), default);
            Assert.Equal(complete.ObservationDigest, page.ObservationDigest);
            rows.AddRange(page.Rows); cursor = page.NextCursor;
            Assert.Equal(cursor is not null, page.HasMore);
            Assert.InRange(rows.Count, 1, complete.Rows.Length);
        } while (cursor is not null);
        Assert.Equal(complete.Rows.Select(r => r.Kind + ":" + r.Id), rows.Select(r => r.Kind + ":" + r.Id));
        Assert.Equal(rows.Count, rows.Select(r => r.Kind + ":" + r.Id).Distinct().Count());
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(complete).Length, 1, 1024 * 1024);
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => service.InspectAsync(new(1, 1, Role.Player, "player"), new(), default));
    }

    [Theory]
    [InlineData(0, null)][InlineData(101, null)][InlineData(1, "not-a-cursor")]
    public async Task Invalid_page_never_changes_the_cut(int limit, string? cursor)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        await OutpostMainObservationTests.Prepare(h, h.MainObservation); string before = await f.Cut();
        await Assert.ThrowsAsync<InvalidDataException>(() => OutpostAdminActivationTests.Service(h).InspectAsync(Actor.Api,
            new(Limit: limit, Cursor: cursor), default));
        Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Unavailable_is_not_an_empty_successful_page_and_never_creates_a_missing_anchor()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        await OutpostMainObservationTests.Prepare(h, h.MainObservation); File.Delete(h.Anchor.DatabasePath);
        f.Host.Dispose(); // Preserve and hash the actual server.lock too; do not exclude locked bytes from the assertion.
        string before = OutpostAdminInspectionTests.Files(h);
        var view = await OutpostAdminActivationTests.Service(h).InspectAsync(Actor.Api, new(), default);
        Assert.Equal(OutpostAuthorityObservationKind.Unavailable, view.Observation.Kind); Assert.Null(view.HasHistory);
        Assert.Empty(view.Rows); Assert.False(view.AdvertiseCapability); Assert.NotEmpty(view.Blockers);
        Assert.Null(view.NextCursor); Assert.False(File.Exists(h.Anchor.DatabasePath));
        Assert.Equal(before, OutpostAdminInspectionTests.Files(h));
    }

    [Fact]
    public async Task Owner_and_operation_filters_preserve_the_exact_original_request_identity()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        var source = await h.Db.OutpostOperations.AsNoTracking().SingleAsync();
        var view = await OutpostAdminActivationTests.Service(h).InspectAsync(Actor.Api, new(1, source.Id), default);
        var row = Assert.Single(view.Rows); Assert.Equal("Operation", row.Kind); Assert.Equal(source.Id, row.Id);
        Assert.Equal(source.RequestId, row.RequestId); Assert.Equal(source.IntentDigest, row.OriginalIntentDigest);
        Assert.Equal(source.WorldId, row.Scope!.WorldInstanceId); Assert.Equal(source.SaveWriteEpoch, row.Scope.SaveWriteEpoch);
        Assert.Empty((await OutpostAdminActivationTests.Service(h).InspectAsync(Actor.Api, new(2, source.Id), default)).Rows);
    }

    [Fact]
    public async Task Http_root_requires_read_but_not_admin_and_never_includes_an_action_grant()
    {
        using var f = new ServerFixture().WithToken(0, "readonly", "outposts-read-tests-token-20261105", "read");
        using var read = f.Rest("outposts-read-tests-token-20261105"); using var anonymous = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/admin/outposts?limit=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/outposts?limit=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync("/api/admin/outposts/preview", new { action = "initialize" })).StatusCode);
    }
}
