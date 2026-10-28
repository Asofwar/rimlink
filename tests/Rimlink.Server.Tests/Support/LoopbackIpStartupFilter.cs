using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Rimlink.Server.Tests.Support;

/// <summary>TestServer has no socket, so RemoteIpAddress is null; make every test call look like it comes from 127.0.0.1.</summary>
public sealed class LoopbackIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
            return nextMiddleware(context);
        });
        next(app);
    };
}
