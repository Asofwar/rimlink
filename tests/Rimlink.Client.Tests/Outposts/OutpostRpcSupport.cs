using System.Collections.Concurrent;
using Rimlink.Client.Net;
using Rimlink.Client.Tests.Support;
using Rimlink.Server.Tests.Support;

namespace Rimlink.Client.Tests.Outposts;

internal sealed class OutpostTraffic(HttpMessageHandler inner, Func<string, HttpResponseMessage?>? intercept = null) : DelegatingHandler(inner)
{
    public ConcurrentQueue<string> Paths { get; } = new();
    public Action<HttpRequestMessage>? Observe { get; init; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string path = request.RequestUri!.AbsolutePath;
        Paths.Enqueue(path);
        Observe?.Invoke(request);
        return intercept?.Invoke(path) is { } reply ? Task.FromResult(reply) : base.SendAsync(request, ct);
    }
    public static async Task<OutpostTestSession> Connect(OutpostClientFixture fixture, OutpostTraffic traffic, string? login = null)
    {
        var connection = new RimlinkConnection(new ServerEndpoint("test", 1), new PasswordCredentials(login ?? fixture.LoginName, "pw"),
            new NoPrompt(), new ConsoleLog(), new ConnectionTransport(fixture.Server.Server.BaseAddress, traffic));
        try { await connection.ConnectAsync(default); return new(connection); }
        catch { await connection.DisposeAsync(); throw; }
    }
}

internal sealed class OutpostTestSession(RimlinkConnection connection) : IAsyncDisposable
{
    public RimlinkConnection Connection => connection;
    public async ValueTask DisposeAsync()
    {
        try { await connection.DisconnectAsync(); }
        finally { await connection.DisposeAsync(); }
    }
}
