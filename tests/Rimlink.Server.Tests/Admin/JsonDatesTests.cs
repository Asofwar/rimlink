using System.Net.Http.Json;
using System.Text.Json;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Admin;

/// <summary>
/// SQLite returns DateTime with Kind=Unspecified; written without an offset, a browser reads UTC as local time (wrong times,
/// a fresh mute looking expired). REST dates carry the UTC marker.
/// </summary>
public class JsonDatesTests
{
    [Fact]
    public async Task DatesFromTheDatabaseCarryTheUtcMarker()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true").WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var rest = fixture.Rest();
        await rest.PostAsJsonAsync($"/api/admin/players/{nova.Player.PlayerId}/mute", new { minutes = 30 });

        var card = await rest.GetFromJsonAsync<JsonElement>($"/api/admin/players/{nova.Player.PlayerId}");
        var action = (await rest.GetFromJsonAsync<JsonElement>("/api/admin/actions")).EnumerateArray().First();

        foreach (string value in new[] { card.GetProperty("createdAt").GetString()!, card.GetProperty("mutedUntil").GetString()!, action.GetProperty("at").GetString()! })
        {
            Assert.EndsWith("Z", value);
            Assert.True(DateTimeOffset.Parse(value) > DateTimeOffset.UtcNow.AddHours(-1), value);
        }
        Assert.True(DateTimeOffset.Parse(card.GetProperty("mutedUntil").GetString()!) > DateTimeOffset.UtcNow.AddMinutes(25));
    }
}
