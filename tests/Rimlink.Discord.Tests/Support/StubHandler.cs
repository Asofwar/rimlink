using System.Net;
using System.Text;

namespace Rimlink.Discord.Tests.Support;

/// <summary>Answers every request with the same JSON body.</summary>
public sealed class StubHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
}
