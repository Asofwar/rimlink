using System.Net;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Auth;

public class SteamNameTests
{
    private static LoginRequest SteamLogin(long steamId) => new()
    {
        ProtocolVersion = ProtocolVersion.Current,
        SteamTicket = ByteString.CopyFrom(FakeSteamVerifier.Ticket(steamId)),
    };

    private static async Task<string> FirstLoginName(long steamId, string? persona)
    {
        if (persona is not null) FakeSteamVerifier.Personas[steamId] = persona;
        using var fixture = new ServerFixture();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());
        return (await auth.LoginAsync(SteamLogin(steamId))).Player.Name;
    }

    [Fact]
    public async Task ANewAccountTakesItsSteamName() =>
        Assert.Equal("Asofwar", await FirstLoginName(76561198000100001, "Asofwar"));

    [Fact]
    public async Task ACyrillicSteamNameIsKept() =>
        Assert.Equal("Вася Пупкин", await FirstLoginName(76561198000100002, "Вася Пупкин"));

    [Fact]
    public async Task CharactersANameCannotHoldAreDropped() =>
        Assert.Equal("Вася Pro", await FirstLoginName(76561198000100003, "★ Вася | Pro ★"));

    [Fact]
    public async Task ALongSteamNameIsCut() =>
        Assert.Equal(new string('x', Limits.NameMaxLength), await FirstLoginName(76561198000100004, new string('x', 60)));

    [Fact]
    public async Task ANameWithNothingUsableFallsBackToTheGeneratedOne() =>
        Assert.Equal("Player0005", await FirstLoginName(76561198000100005, "★★★"));

    [Fact]
    public async Task NoSteamNameFallsBackToTheGeneratedOne() =>
        Assert.Equal("Player0006", await FirstLoginName(76561198000100006, null));

    [Fact]
    public async Task ATakenSteamNameGetsASuffixAndStaysWithinTheLimit()
    {
        string name = new('y', Limits.NameMaxLength);
        FakeSteamVerifier.Personas[76561198000100007] = name;
        FakeSteamVerifier.Personas[76561198000100008] = name;
        using var fixture = new ServerFixture();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var first = await auth.LoginAsync(SteamLogin(76561198000100007));
        var second = await auth.LoginAsync(SteamLogin(76561198000100008));

        Assert.Equal(name, first.Player.Name);
        Assert.NotEqual(first.Player.Name, second.Player.Name);
        Assert.EndsWith("-2", second.Player.Name);
        Assert.True(second.Player.Name.Length <= Limits.NameMaxLength);
    }

    [Fact]
    public async Task ALaterLoginKeepsTheNameTheAccountHas()
    {
        const long steamId = 76561198000100009;
        FakeSteamVerifier.Personas[steamId] = "First";
        using var fixture = new ServerFixture();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        await auth.LoginAsync(SteamLogin(steamId));
        FakeSteamVerifier.Personas[steamId] = "Second";
        var again = await auth.LoginAsync(SteamLogin(steamId));

        Assert.Equal("First", again.Player.Name);
    }

    [Fact]
    public async Task TheWebApiVerifierReadsThePersonaName()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, """{"response":{"players":[{"steamid":"1","personaname":"Asofwar"}]}}"""));
        var verifier = new SteamWebApiTicketVerifier(http, Options.Create(new AuthOptions { SteamWebApiKey = "key" }));

        Assert.Equal("Asofwar", await verifier.PersonaName(1, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"response":{"players":[]}}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    public async Task TheWebApiVerifierGivesNoNameWhenSteamHasNone(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler(status, body));
        var verifier = new SteamWebApiTicketVerifier(http, Options.Create(new AuthOptions { SteamWebApiKey = "key" }));

        Assert.Null(await verifier.PersonaName(1, default));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
