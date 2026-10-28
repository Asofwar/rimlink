using System.Collections.Concurrent;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Rimlink.Server.Hosting;

/// <summary>
/// Kestrel connection middleware: at most <see cref="MaxPerIp"/> connections per IP that have not logged in,
/// and a connection that has not logged in within <see cref="LoginTimeout"/> is closed.
/// A login marks its connection via <see cref="MarkAuthenticated"/>.
/// </summary>
public static class UnauthenticatedConnectionLimiter
{
    public const int MaxPerIp = 5;
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(30);
    private const string AuthenticatedKey = "rimlink.authenticated";

    private static readonly ConcurrentDictionary<string, int> PendingPerIp = new();

    public static void Install(ListenOptions listen) => listen.Use(next => async connection =>
    {
        string ip = connection.RemoteEndPoint?.ToString()?.Split(':')[0] ?? "unknown";
        if (PendingPerIp.AddOrUpdate(ip, 1, (_, n) => n + 1) > MaxPerIp)
        {
            PendingPerIp.AddOrUpdate(ip, 0, (_, n) => n - 1);
            connection.Abort(new ConnectionAbortedException("too many connections waiting for login"));
            return;
        }

        bool released = false;
        void Release()
        {
            if (released) return;
            released = true;
            PendingPerIp.AddOrUpdate(ip, 0, (_, n) => Math.Max(0, n - 1));
        }

        using var timer = new Timer(_ =>
        {
            if (IsAuthenticated(connection)) Release();
            else connection.Abort(new ConnectionAbortedException("login timeout"));
        }, null, LoginTimeout, Timeout.InfiniteTimeSpan);

        try
        {
            await next(connection);
        }
        finally
        {
            Release();
        }
    });

    /// <summary>Called after a successful login so the connection stops counting as pending.</summary>
    public static void MarkAuthenticated(HttpContext http)
    {
        var items = http.Features.Get<IConnectionItemsFeature>()?.Items;
        if (items is not null) items[AuthenticatedKey] = true;
    }

    private static bool IsAuthenticated(ConnectionContext connection) =>
        connection.Items.TryGetValue(AuthenticatedKey, out var v) && v is true;
}
