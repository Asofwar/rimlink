using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminRestTests
{
    private const string Root = "/api/admin/outposts";

    [Fact]
    public async Task Inspect_and_preview_use_the_real_readonly_service_and_commit_replays_the_exact_original_command()
    {
        using var f = await LifecycleFixture.Create(); using var client = f.Host.Rest();
        var before = await f.Cut();
        var observed = await client.GetFromJsonAsync<JsonElement>(Root + "/authority");
        Assert.Equal((int)OutpostAuthorityObservationKind.Committed, observed.GetProperty("kind").GetInt32());
        Assert.Equal((int)OutpostDomainFormat.V1, observed.GetProperty("committed").GetProperty("domainFormat").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", observed.GetProperty("committed").GetProperty("headDigest").GetString()!);
        var previewResponse = await client.PostAsJsonAsync(Root + "/preview", new { action = "activate-domain-format",
            requestId = Guid.NewGuid().ToString("D"), reason = "Activate the verified domain format" });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<WireCommand>(); Assert.NotNull(preview);
        Assert.NotNull(preview.Command); Assert.NotNull(preview.IntentDigest); Assert.False(preview.Confirmed); Assert.Empty(preview.Blockers);
        var intent = OutpostAdminIntentCodec.Decode(preview.Command);
        Assert.Equal(Actor.Api, intent.Actor); Assert.Equal(Convert.ToHexStringLower(OutpostAdminIntentCodec.Digest(intent)), preview.IntentDigest);
        Assert.Equal(before, await f.Cut());
        var body = new { command = preview.Command, intentDigest = preview.IntentDigest, confirmed = true };
        var committed = await client.PostAsJsonAsync(Root + "/activate-domain-format", body); Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var first = await committed.Content.ReadFromJsonAsync<JsonElement>(); Assert.False(first.GetProperty("replay").GetBoolean());
        var cut = await f.Cut();
        var replay = await client.PostAsJsonAsync(Root + "/activate-domain-format", body); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var second = await replay.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(second.GetProperty("replay").GetBoolean());
        Assert.Equal(first.GetProperty("head").GetRawText(), second.GetProperty("head").GetRawText()); Assert.Equal(cut, await f.Cut());
        Assert.Single(await f.H.Db.OutpostAdminCommands.Where(c => c.Action == "ActivateDomainFormat").ToArrayAsync());
        Assert.Single(await f.H.Db.AdminActions.Where(c => c.Action == "outposts.ActivateDomainFormat").ToArrayAsync());
    }

    [Fact]
    public async Task Manifest_preview_and_commit_are_the_same_canonical_service_not_the_legacy_confirmation_route()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        await OutpostAdminActivationTests.Service(h).ActivateDomainFormatAsync(Actor.Api,
            await OutpostAdminActivationTests.CommandForCurrent(h), default);
        var source = await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync(m => m.Active);
        var document = Rimlink.Protocol.V1.OutpostManifestDocument.Parser.ParseFrom(source.Document); document.ValuationVersion = "rest-admin";
        var manifests = new OutpostManifestService(h.Db, h.Authority, new OutpostClock(f.H.F.Time), h.Files);
        var staged = await manifests.StageAsync(Actor.Api, document, default);
        using var client = f.Host.Rest(); var before = await f.Cut();
        var response = await client.PostAsJsonAsync(Root + "/preview", new { action = "confirm-manifest", requestId = Guid.NewGuid().ToString("D"),
            reason = "Confirm exact planet evidence", manifestId = staged.ManifestId, manifestDigest = staged.Digest.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<WireCommand>(); Assert.NotNull(preview!.Command);
        Assert.Equal(before, await f.Cut());
        var result = await client.PostAsJsonAsync(Root + "/manifests/" + staged.ManifestId + "/confirm", new { command = preview.Command, intentDigest = preview.IntentDigest, confirmed = true });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Single(await f.H.Db.OutpostAdminCommands.Where(c => c.Action == "ConfirmManifest").ToArrayAsync());
        Assert.True((await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync(m => m.Id == staged.ManifestId)).Active);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Authentication_is_required_before_any_authority_action(bool post)
    {
        using var f = await LifecycleFixture.Create(); using var client = f.Host.CreateClient(); string before = await f.Cut();
        var response = post ? await client.PostAsJsonAsync(Root + "/preview", new { action = "initialize", requestId = Guid.NewGuid().ToString("D"), reason = "x" })
            : await client.GetAsync(Root + "/authority");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Read_scope_does_not_authorize_authority_inspection_or_commands()
    {
        using var f = new ServerFixture().WithToken(0, "readonly", "readonly-outpost-test-token", "read");
        using var client = f.Rest("readonly-outpost-test-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Root + "/authority")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Root + "/preview", new { action = "initialize" })).StatusCode);
    }

    [Theory]
    [InlineData("actor")][InlineData("confirmation")][InlineData("digest")]
    public async Task Body_or_header_cannot_substitute_the_preview_actor_digest_or_confirmation(string defect)
    {
        using var f = await LifecycleFixture.Create(); using var client = f.Host.Rest();
        var response = await client.PostAsJsonAsync(Root + "/preview", new { action = "activate-domain-format", requestId = Guid.NewGuid().ToString("D"), reason = "Verify exact actor" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var command = await response.Content.ReadFromJsonAsync<WireCommand>(); var digest = command!.IntentDigest!;
        if (defect == "digest") digest = (digest[0] == '0' ? "1" : "0") + digest[1..];
        if (defect == "actor") client.DefaultRequestHeaders.Add("X-Rimlink-Actor", "different-authenticated-label");
        var before = await f.Cut();
        var result = await client.PostAsJsonAsync(Root + "/activate-domain-format", new { command = command.Command, intentDigest = digest, confirmed = defect != "confirmation" });
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode); Assert.Equal(before, await f.Cut());
    }

    [Theory]
    [InlineData("duplicate", 400)][InlineData("unknown-property", 400)][InlineData("oversize", 413)][InlineData("truncated", 400)]
    public async Task Malformed_or_oversized_transport_never_reaches_the_domain_transaction(string kind, int status)
    {
        using var f = await LifecycleFixture.Create(); using var client = f.Host.Rest(); var before = await f.Cut();
        string json = kind switch { "duplicate" => "{\"action\":\"initialize\",\"action\":\"initialize\"}",
            "unknown-property" => JsonSerializer.Serialize(new { action = "initialize", requestId = Guid.NewGuid().ToString("D"), reason = "x", actor = new { role = 2 } }),
            "oversize" => new string(' ', 2 * 1024 * 1024 + 1), _ => "{" };
        var result = await client.PostAsync(Root + "/preview", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)status, result.StatusCode); Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Ambiguous_case_aliases_are_not_a_second_way_to_select_the_preview_action()
    {
        using var f = await LifecycleFixture.Create(); using var client = f.Host.Rest(); var before = await f.Cut();
        string json = "{\"action\":\"initialize\",\"requestId\":\"" + Guid.NewGuid().ToString("D") + "\",\"reason\":\"x\",\"ACTION\":\"activate-domain-format\"}";
        var response = await client.PostAsync(Root + "/preview", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Body_limit_is_enforced_without_a_content_length_before_any_preview_or_audit()
    {
        using var f = await LifecycleFixture.Create(); using var client = f.Host.Rest(); var before = await f.Cut();
        using var content = new UnknownLengthJson(Encoding.UTF8.GetBytes(new string(' ', 2 * 1024 * 1024 + 1)));
        var response = await client.PostAsync(Root + "/preview", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); Assert.Equal(before, await f.Cut());
    }

    [Theory]
    [InlineData("route", 409)][InlineData("truncated-command", 400)][InlineData("oversize-command", 400)]
    [InlineData("upper-hash", 400)][InlineData("bad-hash", 400)][InlineData("missing-confirmation", 400)]
    public async Task Canonical_command_transport_does_not_reinterpret_malformed_or_wrong_action_bytes(string defect, int status)
    {
        using var f = await LifecycleFixture.Create(); using var client = f.Host.Rest();
        var previewResponse = await client.PostAsJsonAsync(Root + "/preview", new { action = "activate-domain-format", requestId = Guid.NewGuid().ToString("D"), reason = "Bound canonical action" });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<WireCommand>(); byte[] command = preview!.Command!; string hash = preview.IntentDigest!;
        if (defect == "truncated-command") command = command[..^1];
        if (defect == "oversize-command") command = new byte[65537];
        if (defect == "upper-hash") hash = hash.ToUpperInvariant();
        if (defect == "bad-hash") hash = new string('z', 64);
        string json = defect == "missing-confirmation" ? JsonSerializer.Serialize(new { command, intentDigest = hash })
            : JsonSerializer.Serialize(new { command, intentDigest = hash, confirmed = true });
        var before = await f.Cut();
        var response = await client.PostAsync(Root + (defect == "route" ? "/initialize" : "/activate-domain-format"), new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.Equal(before, await f.Cut());
    }

    private sealed class UnknownLengthJson : HttpContent
    {
        private readonly byte[] bytes;
        public UnknownLengthJson(byte[] bytes) { this.bytes = bytes; Headers.ContentType = new("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => stream.WriteAsync(bytes, token).AsTask();
    }

    private sealed record WireCommand(byte[]? Command, string? IntentDigest, bool Confirmed, string[] Blockers);
}
