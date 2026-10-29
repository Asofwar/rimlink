using System.Net.Http.Headers;
using Rimlink.Discord.Api;
using Rimlink.Server.Tests.Support;

namespace Rimlink.Discord.Tests.Support;

/// <summary>
/// A real in-process Rimlink server with the bot's scoped token <c>discord-bot</c> (scope admin) and, on request, narrower
/// tokens; hands out <see cref="RimlinkApi"/> clients and feed clients over the test server.
/// </summary>
public sealed class BotFixture : IDisposable
{
    public const string BotToken = "discord-bot-token-0123456789";

    public BotFixture(Action<ServerFixture>? configure = null, params (string Name, string Token, string[] Scopes)[] tokens)
    {
        Fixture = new ServerFixture()
            .WithSetting("Auth:AllowPassword", "true")
            .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
            .WithToken(0, "discord-bot", BotToken, "admin");
        for (int i = 0; i < tokens.Length; i++) Fixture.WithToken(i + 1, tokens[i].Name, tokens[i].Token, tokens[i].Scopes);
        configure?.Invoke(Fixture);
    }

    public ServerFixture Fixture { get; }

    public HttpClient Client(string? token = null, DelegatingHandler? spy = null, TimeSpan? timeout = null)
    {
        HttpMessageHandler handler = Fixture.Server.CreateHandler();
        if (spy is not null)
        {
            spy.InnerHandler = handler;
            handler = spy;
        }
        var client = new HttpClient(handler) { BaseAddress = new Uri(Fixture.Server.BaseAddress, "api/admin/"), Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? BotToken);
        return client;
    }

    public RimlinkApi Api(string? token = null, DelegatingHandler? spy = null) => new(Client(token, spy));

    public Func<HttpClient> FeedClient(string? token = null) => () => Client(token, timeout: Timeout.InfiniteTimeSpan);

    public void Dispose() => Fixture.Dispose();
}

/// <summary>Counts requests that reach the server.</summary>
public sealed class CountingHandler : DelegatingHandler
{
    public int Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Count);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Fails every request as an unreachable server would.</summary>
public sealed class DownHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("connection refused");
}
