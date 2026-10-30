using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Auth;

public class SteamLoginTests
{
    private const long SteamId = 76561198000000001;
    private const long OwnerId = 76561198000000002;

    private static LoginRequest SteamLogin(byte[] ticket) => new()
    {
        ProtocolVersion = ProtocolVersion.Current,
        SteamTicket = ByteString.CopyFrom(ticket),
    };

    [Fact]
    public async Task ValidTicketCreatesAccountAndLogsIn()
    {
        using var fixture = new ServerFixture();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var reply = await auth.LoginAsync(SteamLogin(FakeSteamVerifier.Ticket(SteamId)));

        Assert.NotEmpty(reply.SessionToken);
        Assert.Equal("Player0001", reply.Player.Name);
        using var scope = fixture.Services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Accounts.SingleAsync();
        Assert.Equal(SteamId, account.SteamId);
    }

    [Fact]
    public async Task SecondLoginReusesTheAccount()
    {
        using var fixture = new ServerFixture();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var first = await auth.LoginAsync(SteamLogin(FakeSteamVerifier.Ticket(SteamId)));
        var second = await auth.LoginAsync(SteamLogin(FakeSteamVerifier.Ticket(SteamId)));

        Assert.Equal(first.Player.PlayerId, second.Player.PlayerId);
    }

    [Fact]
    public async Task InvalidTicketIsUnauthenticated()
    {
        using var fixture = new ServerFixture();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(SteamLogin([1, 2, 3])).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Equal(ErrorCode.SteamTicketRejected, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task AServerWithoutASteamKeyDoesNotCallItARejectedTicket()
    {
        using var fixture = new ServerFixture();
        ((FakeSteamVerifier)fixture.Services.GetRequiredService<ISteamTicketVerifier>()).Configured = false;
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(SteamLogin([1, 2, 3])).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.NotEqual(ErrorCode.SteamTicketRejected, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public void TheWebApiVerifierIsConfiguredByItsKey()
    {
        static SteamWebApiTicketVerifier With(string key) => new(new HttpClient(), Options.Create(new AuthOptions { SteamWebApiKey = key }));

        Assert.True(With("key").Configured);
        Assert.False(With(" ").Configured);
    }

    [Fact]
    public async Task OwnerSteamIdIsStoredForFamilySharing()
    {
        using var fixture = new ServerFixture();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        await auth.LoginAsync(SteamLogin(FakeSteamVerifier.Ticket(SteamId, OwnerId)));

        using var scope = fixture.Services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Accounts.SingleAsync();
        Assert.Equal(OwnerId, account.OwnerSteamId);
    }

    [Fact]
    public async Task AdminSteamIdFromConfigGetsAdminRole()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AdminSteamIds:0", SteamId.ToString());
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var reply = await auth.LoginAsync(SteamLogin(FakeSteamVerifier.Ticket(SteamId)));

        Assert.Equal(Role.Admin, reply.Role);
    }

    [Fact]
    public async Task SteamWebApiTicketVerifierParsesOkResponse()
    {
        string? requestedUrl = null;
        var handler = new StubHandler(request =>
        {
            requestedUrl = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"response":{"params":{"result":"OK","steamid":"76561198000000001","ownersteamid":"76561198000000002","vacbanned":false,"publisherbanned":false}}}"""),
            };
        });
        var verifier = new SteamWebApiTicketVerifier(new HttpClient(handler), Options.Create(new AuthOptions { SteamWebApiKey = "KEY" }));

        var identity = await verifier.Verify([0xAB, 0xCD], CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal(SteamId, identity.SteamId);
        Assert.Equal(OwnerId, identity.OwnerSteamId);
        Assert.Contains("appid=294100", requestedUrl);
        Assert.Contains("ticket=ABCD", requestedUrl);
        Assert.Contains("key=KEY", requestedUrl);
    }

    [Fact]
    public async Task SteamWebApiTicketVerifierReturnsNullOnError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"response":{"error":{"errorcode":3,"errordesc":"Invalid parameter"}}}"""),
        });
        var verifier = new SteamWebApiTicketVerifier(new HttpClient(handler), Options.Create(new AuthOptions { SteamWebApiKey = "KEY" }));

        Assert.Null(await verifier.Verify([1], CancellationToken.None));
    }

    [Fact]
    public async Task SteamWebApiTicketVerifierWithoutKeyReturnsNull()
    {
        var verifier = new SteamWebApiTicketVerifier(new HttpClient(new StubHandler(_ => throw new InvalidOperationException("must not be called"))), Options.Create(new AuthOptions()));

        Assert.Null(await verifier.Verify([1], CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
