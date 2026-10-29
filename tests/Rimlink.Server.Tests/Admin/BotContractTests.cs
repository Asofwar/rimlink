using System.Net.Http.Json;
using System.Text.Json;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Admin;

/// <summary>
/// The admin API fields the Discord bot deserializes (bot spec §3: the panel owns these routes). A panel change that
/// renames or drops one of them breaks the bot; this test makes that visible on the server side.
/// </summary>
public class BotContractTests
{
    private const string BotToken = "discord-bot-token-0123456789";

    [Fact]
    public async Task FieldsTheBotReadsExist()
    {
        using var fixture = new ServerFixture()
            .WithSetting("Auth:AllowPassword", "true")
            .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
            .WithToken(0, "discord-bot", BotToken, "admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await new Protocol.V1.World.WorldClient(fixture.CreateChannel()).SettleAsync(new SettleRequest { Tile = 100, Name = "Nova Home" }, Bearer(nova.SessionToken));
        await new Protocol.V1.Chat.ChatClient(fixture.CreateChannel()).SendAsync(new SendRequest { Text = "hello" }, Bearer(nova.SessionToken));
        var bot = fixture.Rest(BotToken, actor: "discord:Mod (1)");
        (await bot.PostAsJsonAsync($"/api/admin/players/{bob.Player.PlayerId}/kick", new { reason = "test" })).EnsureSuccessStatusCode();

        var list = await bot.GetFromJsonAsync<JsonElement>("/api/admin/players?q=no&limit=25");
        Assert.True(list.GetProperty("total").GetInt32() >= 1);
        var item = list.GetProperty("items").EnumerateArray().First(i => i.GetProperty("name").GetString() == "Nova");
        foreach (string field in new[] { "id", "factionName", "online", "role" }) Assert.True(item.TryGetProperty(field, out _), $"players item lacks {field}");
        Assert.True(item.GetProperty("online").GetBoolean());

        var card = await bot.GetFromJsonAsync<JsonElement>($"/api/admin/players/{nova.Player.PlayerId}");
        foreach (string field in new[] { "name", "factionName", "online", "peaceful" }) Assert.True(card.TryGetProperty(field, out _), $"card lacks {field}");
        Assert.Equal("Nova Home", card.GetProperty("settlements").EnumerateArray().Single().GetProperty("name").GetString());

        var status = await bot.GetFromJsonAsync<JsonElement>("/api/admin/status");
        foreach (string field in new[] { "version", "playersOnline", "maxPlayers", "worldExists", "certificateFingerprint" }) Assert.True(status.TryGetProperty(field, out _), $"status lacks {field}");
        foreach (string part in new[] { "economy", "raids" })
        {
            Assert.True(status.GetProperty(part).TryGetProperty("frozen", out _), $"status.{part} lacks frozen");
            Assert.True(status.GetProperty(part).TryGetProperty("reason", out _), $"status.{part} lacks reason");
        }

        var actions = await bot.GetFromJsonAsync<JsonElement>("/api/admin/actions?limit=1");
        Assert.Equal("api:discord-bot:discord:Mod (1)", actions.EnumerateArray().First().GetProperty("actorName").GetString());

        var chat = await bot.GetFromJsonAsync<JsonElement>("/api/admin/chat?after=0&limit=50");
        var line = chat.EnumerateArray().First(l => l.GetProperty("text").GetString() == "hello");
        Assert.Equal("Nova", line.GetProperty("fromName").GetString());
        Assert.Equal("player", line.GetProperty("kind").GetString());
        Assert.True(line.TryGetProperty("id", out _));
    }
}
