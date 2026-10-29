using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.HygieneFixtures;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Tests.Admin;

/// <summary>Listing, archiving and restoring colonies through REST and the text command (spec §5).</summary>
public class ArchiveAdminTests
{
    private const string ReadToken = "read-token-0123456789-abcdef";
    private const string ModToken = "moderate-token-0123456789-abc";

    private static void Offline(ServerFixture fixture, long playerId)
    {
        var sessions = fixture.Services.GetRequiredService<SessionRegistry>();
        if (sessions.ByPlayer(playerId) is { } s) sessions.End(s.Token);
    }

    private static async Task<long> ArchiveThroughRest(ServerFixture fixture, long playerId)
    {
        var response = await fixture.Rest().PostAsync($"/api/admin/players/{playerId}/archive", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task<(HttpStatusCode Status, string Detail)> Problem(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        string detail = body.Length > 0 && JsonDocument.Parse(body).RootElement.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
        return (response.StatusCode, detail);
    }

    [Fact]
    public async Task ListShowsArchivedColonies()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, wealth: 70000, tick: 5 * GameDay);
        var mira = await Settled(fixture, "mira", tile: 200);
        Offline(fixture, nova.PlayerId);
        Offline(fixture, mira.PlayerId);
        long novaArchive = await ArchiveThroughRest(fixture, nova.PlayerId);
        long miraArchive = await ArchiveThroughRest(fixture, mira.PlayerId);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Rest().PostAsync($"/api/admin/archive/{miraArchive}/restore", null)).StatusCode);

        var archived = (await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/archive")).EnumerateArray().ToList();
        var all = (await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/archive?state=all")).EnumerateArray().ToList();

        var row = Assert.Single(archived);
        Assert.Equal(novaArchive, row.GetProperty("id").GetInt64());
        Assert.Equal(nova.PlayerId, row.GetProperty("playerId").GetInt64());
        Assert.Equal(("Nova", "Nova Home", 100), (row.GetProperty("playerName").GetString(), row.GetProperty("colony").GetString(), row.GetProperty("tile").GetInt32()));
        Assert.Equal(("admin", "api", "archived"), (row.GetProperty("reason").GetString(), row.GetProperty("archivedBy").GetString(), row.GetProperty("state").GetString()));
        Assert.Equal(70000, row.GetProperty("wealthTotal").GetInt64());
        Assert.Equal(5, row.GetProperty("colonyGameDays").GetInt32());
        Assert.EndsWith("Z", row.GetProperty("archivedAt").GetString());
        Assert.Equal(2, all.Count);
        var restored = all.Single(r => r.GetProperty("id").GetInt64() == miraArchive);
        Assert.Equal(("restored", "api"), (restored.GetProperty("state").GetString(), restored.GetProperty("restoredBy").GetString()));
    }

    [Fact]
    public async Task ArchiveAndRestoreThroughRestAreAudited()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        Offline(fixture, nova.PlayerId);

        long id = await ArchiveThroughRest(fixture, nova.PlayerId);
        var restore = await fixture.Rest().PostAsync($"/api/admin/archive/{id}/restore", null);

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var actions = await Db(fixture, db => db.AdminActions.AsNoTracking().OrderBy(a => a.Id).Select(a => new { a.Action, a.ActorName }).ToListAsync());
        Assert.Contains(actions, a => a.Action == "archive" && a.ActorName == "api");
        Assert.Contains(actions, a => a.Action == "restore" && a.ActorName == "api");
    }

    [Fact]
    public async Task RefusalsMapToStatusCodes()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        var plain = await fixture.RegisterAndLogin("plain", playerName: "Plain");
        Offline(fixture, nova.PlayerId);
        long id = await ArchiveThroughRest(fixture, nova.PlayerId);
        await Settled(fixture, "mira", tile: 100);

        Assert.Equal((HttpStatusCode.Conflict, "no colony"), await Problem(await fixture.Rest().PostAsync($"/api/admin/players/{plain.Player.PlayerId}/archive", null)));
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Rest().PostAsync("/api/admin/players/999999/archive", null)).StatusCode);
        Assert.Equal((HttpStatusCode.NotFound, "unknown archive"), await Problem(await fixture.Rest().PostAsync("/api/admin/archive/999999/restore", null)));
        Assert.Equal((HttpStatusCode.Conflict, "tile taken"), await Problem(await fixture.Rest().PostAsync($"/api/admin/archive/{id}/restore", null)));
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Rest().GetAsync("/api/admin/archive?state=bogus")).StatusCode);
    }

    [Fact]
    public async Task ScopesAreEnforced()
    {
        using var fixture = HygieneServer().WithToken(0, "reader", ReadToken, "read").WithToken(1, "mod", ModToken, "moderate");
        var nova = await Settled(fixture, "nova", tile: 100);

        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Rest(ReadToken).GetAsync("/api/admin/archive")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Rest(ModToken).GetAsync("/api/admin/archive")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Rest(ModToken).PostAsync($"/api/admin/players/{nova.PlayerId}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Rest(ModToken).PostAsync("/api/admin/archive/1/restore", null)).StatusCode);
    }

    [Fact]
    public async Task TextCommandArchivesListsAndRestores()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        Offline(fixture, nova.PlayerId);
        using var scope = fixture.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<AdminCommandRunner>();
        Protocol.V1.AdminCommand Cmd(params string[] args) { var c = new Protocol.V1.AdminCommand { Name = "archive" }; c.Args.AddRange(args); return c; }

        string archived = await runner.Execute(Actor.Api, Cmd("Nova"));
        string list = await runner.Execute(Actor.Api, Cmd("list"));
        long id = await Db(fixture, db => db.ArchivedColonies.Select(c => c.Id).SingleAsync());
        var moderator = new Actor(0, 0, Role.Moderator, "mod");
        await Assert.ThrowsAsync<RpcException>(() => runner.Execute(moderator, Cmd("restore", id.ToString())));
        string restored = await runner.Execute(Actor.Api, Cmd("restore", id.ToString()));

        Assert.Contains("Nova Home", archived);
        Assert.Contains($"#{id}", list);
        Assert.Contains("Nova Home", list);
        Assert.Contains("restored", restored);
        Assert.Contains("archive", AdminCommandRunner.Help);
    }
}
