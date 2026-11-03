using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Influence;

public sealed class InfluenceAdminTests
{
    [Fact]
    public async Task ReadScopeSeesOnlyPublicSourcesAndCannotDisable()
    {
        using var test = await InfluenceFixtures.Create();
        using var read = test.Server.Rest("read-token-for-influence-0001");
        var snapshot = await read.GetFromJsonAsync<JsonElement>("/api/admin/influence");
        Assert.Equal(0, snapshot.GetProperty("sources").GetArrayLength());
        Assert.False(snapshot.TryGetProperty("members", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync($"/api/admin/influence/settings/{test.HomeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync($"/api/admin/influence/settings/{test.HomeId}/disable", new { })).StatusCode);
    }

    [Fact]
    public async Task AdminCanDisableButCannotEnableAndReplayDoesNotDuplicateAudit()
    {
        using var test = await InfluenceFixtures.Create();
        Assert.Null((await test.Send(InfluenceFixtures.Publication(await test.ConfirmSurface(), true))).Error);
        using var admin = test.Server.Rest();
        var setting = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/influence/settings/{test.HomeId}");
        var body = new { requestId = Guid.NewGuid().ToString("N"), expectedVersion = setting.GetProperty("version").GetInt64(), context = setting.GetProperty("context") };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/influence/settings/{test.HomeId}/disable", body)).StatusCode);
        long auditCount = 0;
        await test.Db(async db => auditCount = await db.AdminActions.LongCountAsync());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/influence/settings/{test.HomeId}/disable", body)).StatusCode);
        await test.Db(async db => Assert.Equal(auditCount, await db.AdminActions.LongCountAsync()));
        Assert.Empty((await test.Snapshot()).Sources);
        Assert.False((await test.Setting()).Enabled);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/influence/settings/{test.HomeId}/disable", new { body.requestId, body.expectedVersion, body.context, enabled = true })).StatusCode);
    }

    [Fact]
    public async Task ChangingColorDoesNotCreatePublicationAndRejectsOutOfRangePalette()
    {
        using var test = await InfluenceFixtures.Create();
        using var admin = test.Server.Rest();
        var setting = await test.Setting();
        var body = new { worldId = setting.Context.WorldInstanceId, requestId = Guid.NewGuid().ToString("N"), paletteIndex = 5 };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/influence/alliances/{test.AllianceId}/color", body)).StatusCode);
        Assert.Empty((await test.Snapshot()).Sources);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/influence/alliances/{test.AllianceId}/color", new { body.worldId, requestId = Guid.NewGuid().ToString("N"), paletteIndex = 16 })).StatusCode);
    }
}
