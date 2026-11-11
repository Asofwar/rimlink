extern alias tools;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.GuildCapture;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using tools::Rimlink.Tools.Commands;
using Xunit;

namespace Rimlink.Server.Tests.Tools;

/// <summary>The admin CLI's command table against a real in-process server.</summary>
public class AdminCommandTests
{
    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);

    private static RestClient Cli(ServerFixture fixture)
    {
        var http = fixture.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, "/api/admin/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminRestExtensions.ApiToken);
        return new RestClient(http);
    }

    [Fact]
    public async Task Guild_capture_inspection_is_admin_only_and_prints_product_admission_without_credentials()
    {
        const string readToken = "test-guild-inspection-read-token";
        using var fixture = Server().WithToken(0, "guild-inspection-read", readToken, "read");
        using var anonymous = fixture.CreateClient();
        using var reader = fixture.Rest(readToken);
        using var cli = Cli(fixture);
        var json = await AdminCommand.Run(cli, "guild-capture", ["inspect"]);
        using var report = JsonDocument.Parse(json);
        Assert.False(report.RootElement.GetProperty("productEnabled").GetBoolean());
        Assert.Empty(report.RootElement.GetProperty("campaigns").EnumerateArray());
        Assert.DoesNotContain(AdminRestExtensions.ApiToken, json);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/guild-capture")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/guild-capture")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync("/api/admin/guild-capture/cancel", new { })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync("/api/admin/guild-capture/freeze", new { })).StatusCode);
        using var admin = fixture.Rest();
        var unreviewed = await admin.PostAsJsonAsync("/api/admin/guild-capture/place", new
        {
            scope = new { worldId = Guid.NewGuid(), boundary = Guid.NewGuid() },
            requestId = Guid.NewGuid(), tile = 0, resource = 1, reason = "Unreviewed placement"
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, unreviewed.StatusCode);
    }

    [Fact]
    public async Task Guild_capture_inspection_reports_the_committed_cut_without_advertising_an_offline_runtime()
    {
        using var fixture = await GuildFixture.Create(twoGuilds: true);
        var (_, _, battle) = await BattlePreparedAdmissionTests.Prepare(fixture);
        await fixture.Place(5);
        var admin = OutpostAdminActivationTests.Service(fixture.H);

        var before = await admin.InspectAsync(Actor.Api, default);
        var report = await admin.InspectGuildCaptureAsync(Actor.Api, fixture.Executors, 1, default);
        var after = await admin.InspectAsync(Actor.Api, default);

        Assert.Equal(OutpostAuthorityObservationKind.Committed, report.Authority);
        Assert.Equal(before.Committed!.HeadId, report.HeadId);
        Assert.Equal(report.HeadId, after.Committed!.HeadId);
        Assert.Equal(fixture.Scope, report.Scope);
        Assert.False(report.TransportAdmitted);
        Assert.False(report.ProductEnabled);
        Assert.True(report.HasMore);
        Assert.Single(report.Outposts);
        Assert.Equal(battle.CampaignId, Assert.Single(report.Campaigns).Id);
        Assert.Equal(battle.BattleId, Assert.Single(report.Battles).Id);
    }

    [Fact]
    public async Task Guild_capture_cancel_posts_the_retained_cli_request_and_returns_the_same_receipt_on_retry()
    {
        using var fixture = await GuildFixture.Create(twoGuilds: true);
        await fixture.EnsureApprovedProfile();
        var target = await fixture.Place(0);
        var consent = await fixture.Service.PreviewConsentAsync(1, 50, fixture.Scope, target.OutpostId, 12, default);
        await fixture.Service.AcceptConsentAsync(1, 50, fixture.Scope, Guid.NewGuid().ToString("D"), target.OutpostId, 12, consent.Digest, default);
        fixture.RegisterExecutor();
        var campaign = await fixture.Service.DeclareCampaignAsync(2,
            new(fixture.Scope, Guid.NewGuid().ToString("D"), 60, target.OutpostId, 1, 2), default);
        var observed = await OutpostAdminActivationTests.Service(fixture.H).InspectAsync(Actor.Api, default);
        var request = JsonSerializer.Serialize(new { scope = fixture.Scope, requestId = Guid.NewGuid().ToString("D"), campaign.CampaignId,
            expectedHeadId = observed.Committed!.HeadId, reason = "Cancel scheduled campaign" }, JsonSerializerOptions.Web);
        var file = Path.Combine(fixture.H.Data.Archive, "cancel-request.json");
        await File.WriteAllTextAsync(file, request);
        using var host = Server().WithServices(services => services.AddScoped<GuildOutpostService>(_ => fixture.Service));
        using var cli = Cli(host);

        string first = await AdminCommand.Run(cli, "guild-capture", ["cancel", file]);
        Assert.Equal(first, await AdminCommand.Run(cli, "guild-capture", ["cancel", file]));
        Assert.Equal(request, await File.ReadAllTextAsync(file));
        using var reply = JsonDocument.Parse(first);
        var terminal = reply.RootElement.GetProperty("terminal");
        Assert.Equal("OperatorCancelled", terminal.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, terminal.GetProperty("battleId").ValueKind);
        Assert.Equal(OutpostStartupState.Ready, (await fixture.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Guild_capture_place_previews_without_writes_and_commits_only_the_confirmed_current_intent()
    {
        using var fixture = await GuildFixture.Create();
        const string otherToken = "test-guild-placement-other-admin";
        using var host = Server().WithToken(0, "guild-placement-other", otherToken, "admin").WithServices(services => services
            .AddScoped<GuildOutpostService>(_ => fixture.Service).AddSingleton(fixture.H.MainObservation));
        using var cli = Cli(host);
        using var otherAdmin = host.Rest(otherToken);
        string draft = Path.Combine(fixture.H.Data.Archive, "placement-draft.json");
        string command = Path.Combine(fixture.H.Data.Archive, "placement-command.json");
        await File.WriteAllTextAsync(draft, """{"tile":0,"resource":1,"reason":"Place a steel outpost"}""");
        string head = (await fixture.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        var preview = await Preview();
        Assert.Equal(head, preview["command"]!["expectedHeadId"]!.GetValue<string>());
        Assert.Empty(await fixture.H.Db.Set<GuildOutpostRow>().ToArrayAsync());
        Assert.Equal(head, (await fixture.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);

        preview["confirmed"] = false;
        await File.WriteAllTextAsync(command, preview.ToJsonString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdminCommand.Run(cli, "guild-capture", ["place", command]));
        preview["confirmed"] = true;
        var foreign = await otherAdmin.PostAsJsonAsync("/api/admin/guild-capture/place", preview);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, foreign.StatusCode);
        var changed = preview.DeepClone();
        changed["command"]!["tile"] = 2;
        await File.WriteAllTextAsync(command, changed.ToJsonString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdminCommand.Run(cli, "guild-capture", ["place", command]));
        await fixture.Place(1);
        await File.WriteAllTextAsync(command, preview.ToJsonString());
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => AdminCommand.Run(cli, "guild-capture", ["place", command]));
        Assert.Contains("guild-placement-preview-stale", stale.Message);

        preview = await Preview();
        preview["confirmed"] = true;
        await File.WriteAllTextAsync(command, preview.ToJsonString());
        string result = await AdminCommand.Run(cli, "guild-capture", ["place", command]);
        Assert.Equal(result, await AdminCommand.Run(cli, "guild-capture", ["place", command]));
        var rows = await fixture.H.Db.Set<GuildOutpostRow>().AsNoTracking().OrderBy(row => row.Tile).ToArrayAsync();
        Assert.Equal(new[] { 0, 1 }, rows.Select(row => row.Tile));
        Assert.All(rows, row => Assert.Equal("Neutral", row.Phase));
        string requestId = preview["command"]!["requestId"]!.GetValue<string>();
        var operation = await fixture.H.Db.Set<GuildCaptureOperation>().AsNoTracking().SingleAsync(o => o.RequestId == requestId);
        using var audit = JsonDocument.Parse(operation.Receipt);
        Assert.Equal("Place a steel outpost", audit.RootElement.GetProperty("Command").GetProperty("Reason").GetString());
        var occupied = await Assert.ThrowsAsync<InvalidOperationException>(Preview);
        Assert.Contains("reserves this tile", occupied.Message);

        async Task<JsonObject> Preview() =>
            JsonNode.Parse(await AdminCommand.Run(cli, "guild-capture", ["place", "preview", draft]))!.AsObject();
    }

    [Fact]
    public async Task Guild_capture_freeze_cli_requires_the_current_cut_and_replays_the_saved_command()
    {
        using var fixture = await GuildFixture.Create();
        using var host = Server().WithServices(services => services.AddScoped<GuildOutpostService>(_ => fixture.Service));
        using var cli = Cli(host);
        string file = Path.Combine(fixture.H.Data.Archive, "freeze-command.json");
        var stale = new GuildFreezeCommand(fixture.Scope, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            true, "Pause for maintenance");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(stale, JsonSerializerOptions.Web));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => AdminCommand.Run(cli, "guild-capture", ["freeze", file]));
        Assert.Contains("guild-freeze-preview-stale", rejected.Message);
        Assert.Null(await GuildAuthorityEvidence.LatestFreezeAsync(fixture.H.Db, fixture.Scope.WorldId, default));

        var head = await fixture.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var command = stale with { ExpectedHeadId = head.HeadId };
        string saved = JsonSerializer.Serialize(command, JsonSerializerOptions.Web);
        await File.WriteAllTextAsync(file, saved);
        string result = await AdminCommand.Run(cli, "guild-capture", ["freeze", file]);
        Assert.Equal(result, await AdminCommand.Run(cli, "guild-capture", ["freeze", file]));
        Assert.Equal(saved, await File.ReadAllTextAsync(file));
        var observed = await OutpostAdminActivationTests.Service(fixture.H)
            .InspectGuildCaptureAsync(Actor.Api, fixture.Executors, 32, default);
        Assert.True(observed.Frozen);
        using var reply = JsonDocument.Parse(result);
        Assert.Equal(reply.RootElement.GetProperty("transitionId").GetString(), observed.FreezeTransitionId);
    }

    [Fact]
    public async Task PlayersAndPlayerPrintJson()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        using var cli = Cli(fixture);

        Assert.Contains("\"Nova\"", await AdminCommand.Run(cli, "players", ["nov"]));
        Assert.Contains("walletBalance", await AdminCommand.Run(cli, "player", [nova.Player.PlayerId.ToString()]));
    }

    [Fact]
    public async Task PlayerFreezeAndUnfreezeRun()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        string id = nova.Player.PlayerId.ToString();
        using var cli = Cli(fixture);

        await AdminCommand.Run(cli, "player-freeze", [id, "dupe", "x"]);
        var frozen = JsonDocument.Parse(await AdminCommand.Run(cli, "player", [id])).RootElement;
        await AdminCommand.Run(cli, "player-unfreeze", [id]);
        var open = JsonDocument.Parse(await AdminCommand.Run(cli, "player", [id])).RootElement;

        Assert.Equal("dupe x", frozen.GetProperty("economyFreezeReason").GetString());
        Assert.Equal(JsonValueKind.Null, open.GetProperty("economyFrozenAt").ValueKind);
    }

    [Fact]
    public async Task RoleRenameSaveAllStatusAndConfigRun()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        string id = nova.Player.PlayerId.ToString();
        using var cli = Cli(fixture);

        await AdminCommand.Run(cli, "role", [id, "moderator"]);
        await AdminCommand.Run(cli, "rename", [id, "Nova2", "Star", "Guild"]);
        await AdminCommand.Run(cli, "saveall", []);
        await AdminCommand.Run(cli, "chat", []);
        Assert.Contains("playersOnline", await AdminCommand.Run(cli, "status", []));
        Assert.DoesNotContain(AdminRestExtensions.ApiToken, await AdminCommand.Run(cli, "config", []));

        var card = JsonDocument.Parse(await AdminCommand.Run(cli, "player", [id])).RootElement;
        Assert.Equal("moderator", card.GetProperty("role").GetString());
        Assert.Equal("Nova2", card.GetProperty("name").GetString());
        Assert.Equal("Star Guild", card.GetProperty("factionName").GetString());
    }

    [Fact]
    public async Task BackupAndBackupGet()
    {
        using var fixture = Server();
        using var cli = Cli(fixture);
        string target = Path.Combine(fixture.DataDir, "downloaded.tar.zst");

        string name = JsonDocument.Parse(await AdminCommand.Run(cli, "backup", [])).RootElement.GetProperty("name").GetString()!;
        Assert.Contains(name, await AdminCommand.Run(cli, "backups", []));

        Assert.Equal($"saved {target}", await AdminCommand.Run(cli, "backup-get", [name, target]));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture.DataDir, "backups", name)), await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task TradesAndTradeCancel()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var trades = new Rimlink.Protocol.V1.Trades.TradesClient(fixture.CreateChannel());
        var trade = await trades.OpenAsync(SocialFixtures.OpenTrade(bob.Player.PlayerId, note: "hi"), GrpcTestExtensions.Bearer(nova.SessionToken));
        using var cli = Cli(fixture);

        Assert.Equal(trade.Id, JsonDocument.Parse(await AdminCommand.Run(cli, "trades", [nova.Player.PlayerId.ToString()])).RootElement[0].GetProperty("id").GetInt64());
        await AdminCommand.Run(cli, "trade", ["cancel", trade.Id.ToString(), "by", "cli"]);

        Assert.Equal("Cancelled", JsonDocument.Parse(await AdminCommand.Run(cli, "trades", [])).RootElement[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownCommandThrows()
    {
        using var fixture = Server();
        using var cli = Cli(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AdminCommand.Run(cli, "nope", []));
    }

    [Fact]
    public async Task InfluenceCommandsReadSettingsDisableAndChangeColor()
    {
        using var fixture = await InfluenceFixtures.Create();
        using var cli = Cli(fixture.Server);
        Assert.Null((await fixture.Send(InfluenceFixtures.Publication(await fixture.ConfirmSurface(), true))).Error);
        Assert.Contains("sources", await AdminCommand.Run(cli, "influence", []));
        var setting = await fixture.Setting();
        Assert.Contains("version", await AdminCommand.Run(cli, "influence-setting", [fixture.HomeId.ToString()]));
        await AdminCommand.Run(cli, "influence-disable", [fixture.HomeId.ToString(), Guid.NewGuid().ToString("N"), setting.Context.WorldInstanceId, "0", fixture.AllianceId.ToString(), setting.Version.ToString()]);
        Assert.Empty((await fixture.Snapshot()).Sources);
        await AdminCommand.Run(cli, "influence-color", [fixture.AllianceId.ToString(), setting.Context.WorldInstanceId, "3", Guid.NewGuid().ToString("N")]);
        foreach (string command in new[] { "influence-setting", "influence-disable", "influence-archive-setting", "influence-archive-disable", "influence-color" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => AdminCommand.Run(cli, command, []));
    }

    [Fact]
    public async Task ArchiveCommandsCallTheArchiveRoutes()
    {
        using var fixture = Server();
        var nova = await HygieneFixtures.Settled(fixture, "nova", tile: 100);
        var sessions = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Rimlink.Server.Application.Sessions.SessionRegistry>(fixture.Services);
        sessions.End(sessions.ByPlayer(nova.PlayerId)!.Token);
        using var cli = Cli(fixture);

        long id = JsonDocument.Parse(await AdminCommand.Run(cli, "archive", [nova.PlayerId.ToString()])).RootElement.GetProperty("id").GetInt64();
        Assert.Contains("Nova Home", await AdminCommand.Run(cli, "archived", []));
        await AdminCommand.Run(cli, "restore-colony", [id.ToString()]);
        Assert.Contains("\"restored\"", await AdminCommand.Run(cli, "archived", ["all"]));
    }
}
