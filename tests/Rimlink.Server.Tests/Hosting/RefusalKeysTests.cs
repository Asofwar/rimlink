using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Grpc;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.LiveFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Hosting;

/// <summary>Live UX spec §5: a refusal carries a key the client translates, with its arguments.</summary>
public class RefusalKeysTests
{
    private static Dictionary<string, string> Keyed(string language) =>
        XDocument.Load(Path.Combine(RepoPaths.Root, "mod", "Languages", language, "Keyed", "Rimlink.xml")).Root!.Elements()
            .ToDictionary(e => e.Name.LocalName, e => e.Value);

    private static int Placeholders(string text) => Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().Count();

    [Fact]
    public void EveryRefusalKeyHasEnglishAndRussianText()
    {
        var keys = typeof(RefusalKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        var english = Keyed("English");
        var russian = Keyed("Russian");

        Assert.NotEmpty(keys);
        foreach (string key in keys)
        {
            string name = "Rimlink.Refusal." + key;
            Assert.True(english.ContainsKey(name), $"{name} is missing in English");
            Assert.True(russian.ContainsKey(name), $"{name} is missing in Russian");
            Assert.True(Placeholders(english[name]) == Placeholders(russian[name]), $"{name}: placeholders differ between English and Russian");
        }
    }

    [Fact]
    public async Task OwnLiveSessionRefusalHasItsKey()
    {
        using var fixture = LiveServer();
        var nova = await Connected(fixture, "nova");
        var bob = await Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.PlayerId);
        await nova.Live.ProposeAsync(Propose(bob.PlayerId, LiveKind.LiveVisit), nova.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => nova.Live.ProposeAsync(Propose(bob.PlayerId, LiveKind.LiveVisit, requestId: "second"), nova.Auth).ResponseAsync);

        var detail = ex.ErrorDetail()!;
        Assert.Equal(("Live.OwnSession", ErrorCode.LiveActive, "you already have a live session"), (detail.Key, detail.Code, detail.Message));
    }

    [Fact]
    public async Task MutedRefusalCarriesTheDate()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        var alice = await fixture.RegisterAndLogin("alice");
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ChatService>().Mute(alice.Player.PlayerId, TimeSpan.FromMinutes(10));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            new Protocol.V1.Chat.ChatClient(fixture.CreateChannel()).SendAsync(new SendRequest { Text = "hi" }, Bearer(alice.SessionToken)).ResponseAsync);

        var detail = ex.ErrorDetail()!;
        Assert.Equal("Chat.Muted", detail.Key);
        Assert.Single(detail.Args);
    }

    [Fact]
    public async Task RateLimitRefusalHasItsKey()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true").WithRateLimits();
        var alice = await fixture.RegisterAndLogin("alice");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        RpcException? refused = null;
        for (int i = 0; i < 10 && refused is null; i++)
        {
            try { await chat.SendAsync(new SendRequest { Text = "hi " + i }, Bearer(alice.SessionToken)); }
            catch (RpcException ex) { refused = ex; }
        }

        Assert.Equal(StatusCode.ResourceExhausted, refused?.StatusCode);
        Assert.Equal("RateLimited", refused!.ErrorDetail()?.Key);
    }
}
