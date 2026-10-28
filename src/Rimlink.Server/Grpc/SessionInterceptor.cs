using Grpc.Core;
using Grpc.Core.Interceptors;
using Rimlink.Server.Application.Sessions;

namespace Rimlink.Server.Grpc;

/// <summary>Every call except Auth.Login/Register must carry "authorization: Bearer &lt;token&gt;" of a live session.</summary>
public sealed class SessionInterceptor(SessionRegistry sessions) : Interceptor
{
    public const string ItemKey = "rimlink.session";

    private static readonly HashSet<string> Anonymous = ["/rimlink.v1.Auth/Login", "/rimlink.v1.Auth/Register"];

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, responseStream, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Authenticate(ServerCallContext context)
    {
        if (Anonymous.Contains(context.Method)) return;

        string? header = context.RequestHeaders.GetValue("authorization");
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw RpcErrors.Unauthenticated();

        string token = header["Bearer ".Length..].Trim();
        if (!sessions.TryGet(token, out var session)) throw RpcErrors.Unauthenticated("session expired");

        context.GetHttpContext().Items[ItemKey] = session;
    }
}

public static class ServerCallContextExtensions
{
    public static Session GetSession(this ServerCallContext context) =>
        context.GetHttpContext().Items[SessionInterceptor.ItemKey] as Session ?? throw RpcErrors.Unauthenticated();

    public static string ClientIp(this ServerCallContext context) =>
        context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
