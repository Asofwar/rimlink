extern alias tools;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using tools::Rimlink.Tools.Commands;
using AdminCommand = tools::Rimlink.Tools.Commands.AdminCommand;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminAdapterTests
{
    [Fact]
    public async Task Remote_cli_preserves_the_frozen_terminal_command_and_replays_one_audit()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); using var http = f.Host.Rest();
        http.BaseAddress = new Uri(http.BaseAddress!, "/api/admin/"); using var cli = new RestClient(http);
        var operation = await f.H.Db.OutpostOperations.AsNoTracking().SingleAsync(o => o.Kind == "Found");
        var reply = OutpostOperationReply.Parser.ParseFrom(operation.Result);
        var draft = new { action = "resolve", requestId = Guid.NewGuid().ToString("D"), reason = "Проверка сохранённого исхода",
            resolution = new { kind = 1, operationId = operation.Id,
                original = new { family = 1, kind = operation.Kind, requestId = operation.RequestId, localOperationId = (string?)null,
                    intentDigest = Convert.ToHexStringLower(operation.IntentDigest), derivedRoute = (object?)null },
                scope = reply.Scope, expectedState = operation.State, revision = reply.AuthoritativeRevision, canonicalSnapshot = (object?)null } };
        string file = Path.Combine(f.H.F.H.H.Root, "admin-command.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web))); string cut = await f.Cut();
        var preview = JsonNode.Parse(await AdminCommand.Run(cli, "outposts", ["preview", "--request", file]))!.AsObject();
        Assert.Equal(cut, await f.Cut()); Assert.NotNull(preview["command"]); Assert.Empty(preview["blockers"]!.AsArray());
        preview["confirmed"] = true; await File.WriteAllTextAsync(file, preview.ToJsonString());
        string retained = await File.ReadAllTextAsync(file);
        var first = JsonDocument.Parse(await AdminCommand.Run(cli, "outposts", ["resolve", "--request", file])).RootElement;
        Assert.True(first.GetProperty("outcome").GetProperty("noReplayAuthorization").GetBoolean());
        cut = await f.Cut();
        var replay = JsonDocument.Parse(await AdminCommand.Run(cli, "outposts", ["resolve", "--request", file])).RootElement;
        Assert.True(replay.GetProperty("replay").GetBoolean()); Assert.Equal(cut, await f.Cut());
        Assert.Equal(first.GetProperty("head").GetRawText(), replay.GetProperty("head").GetRawText());
        Assert.Equal(retained, await File.ReadAllTextAsync(file));
        Assert.Single(await f.H.Db.AdminActions.Where(a => a.Action == "outposts.Resolve").ToArrayAsync());
        var inspection = JsonDocument.Parse(await AdminCommand.Run(cli, "outposts", ["inspect", "--player", "1", "--limit", "1"])).RootElement;
        Assert.True(inspection.GetProperty("canAdmin").GetBoolean()); Assert.Single(inspection.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task Manifest_summary_has_fingerprints_and_counts_but_no_document_and_old_confirmation_cannot_bypass_the_gate()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); using var client = f.Host.Rest();
        var manifest = await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync(m => m.Active);
        string cut = await f.Cut();
        var view = await client.GetFromJsonAsync<JsonElement>("/api/admin/outposts/manifests/" + manifest.Id);
        Assert.Equal(manifest.Id, view.GetProperty("manifestId").GetString());
        Assert.Equal(Convert.ToHexStringLower(manifest.Digest), view.GetProperty("digest").GetString());
        Assert.False(view.TryGetProperty("document", out _)); Assert.True(view.GetProperty("affectedOutposts").GetInt32() > 0);
        Assert.Equal(cut, await f.Cut());
        var old = await client.PostAsJsonAsync("/api/admin/outposts/manifest/confirm", new { manifestId = manifest.Id, expectedDigest = Convert.ToHexStringLower(manifest.Digest) });
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode); Assert.Equal(cut, await f.Cut());
    }

    [Fact]
    public async Task Sidecar_is_bounded_and_cannot_be_smuggled_into_initialization_or_an_unknown_resolution()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); using var client = f.Host.Rest(); string cut = await f.Cut();
        foreach (string action in new[] { "initialize", "resolve" })
        {
            var response = await client.PostAsJsonAsync("/api/admin/outposts/preview", new { action, requestId = Guid.NewGuid().ToString("D"),
                reason = "Reject unrelated original input", originalInput = Convert.ToBase64String(new byte[1024 * 1024 + 1]) });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(cut, await f.Cut());
        }
    }
}
