using System.Net;
using System.Net.Http.Json;
using Rimlink.Client.Mods.Delivery;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.ModpackDelivery;
using Xunit;

namespace Rimlink.Client.Tests.Mods;

public sealed class DeliveryDownloadTests
{
    [Fact]
    public async Task ARedirectNeverBecomesAnEmptySuccessfulDownload()
    {
        var endpoint = new ServerEndpoint("example.invalid", 25560);
        using var client = new ModpackDeliveryClient(endpoint, new CertificatePinStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")), new RefusePin(),
            new ConnectionTransport(endpoint.Address, new ReplyHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.invalid/") } })));
        Assert.Equal("Redirect", (await Assert.ThrowsAsync<DeliveryException>(() => client.Capabilities(CancellationToken.None))).Code);
    }

    [Fact]
    public async Task AccessCannotSendASecretBeforeTheCapabilitiesProbe()
    {
        int requests = 0; var endpoint = new ServerEndpoint("example.invalid", 25560);
        using var client = new ModpackDeliveryClient(endpoint, new CertificatePinStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")), new RefusePin(),
            new ConnectionTransport(endpoint.Address, new ReplyHandler(_ => { requests++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) }; })));
        Assert.Equal("TrustFirst", (await Assert.ThrowsAsync<DeliveryException>(() => client.Access("secret", CancellationToken.None))).Code);
        Assert.Equal(0, requests);
    }

    internal sealed class RefusePin : IPinPrompt { public Task<bool> Confirm(ServerEndpoint endpoint, string fingerprint, string? previous) => Task.FromResult(false); }
    internal sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
