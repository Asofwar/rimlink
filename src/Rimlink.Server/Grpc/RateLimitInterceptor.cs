using System.Threading.RateLimiting;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Configuration;
using Rimlink.Server.Application.Sessions;

namespace Rimlink.Server.Grpc;

/// <summary>Fixed-window limiter per (caller, method). Runs after <see cref="SessionInterceptor"/>, so the session is known.</summary>
public sealed class RateLimitInterceptor : Interceptor, IDisposable
{
    private readonly PartitionedRateLimiter<(string Caller, string Method)> limiter;

    public RateLimitInterceptor(IConfiguration configuration)
    {
        // Tests shrink the default window to make the generic limit observable.
        int? testPermits = configuration.GetValue<int?>("RateLimit:Test");
        limiter = PartitionedRateLimiter.Create<(string Caller, string Method), (string, string)>(key =>
        {
            var (permits, window) = RateLimitPolicy.For(key.Method);
            if (testPermits is { } p && RateLimitPolicy.For(key.Method) == RateLimitPolicy.Default) permits = p;
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await Acquire(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await Acquire(context);
        await continuation(request, responseStream, context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await Acquire(context);
        return await continuation(requestStream, context);
    }

    private async Task Acquire(ServerCallContext context)
    {
        // Anonymous calls (Login, Register) are keyed by IP, everything after login by account.
        string caller = context.GetHttpContext().Items[SessionInterceptor.ItemKey] is Session session
            ? "acct:" + session.AccountId
            : "ip:" + context.ClientIp();

        using var lease = await limiter.AcquireAsync((caller, context.Method), permitCount: 1, context.CancellationToken);
        if (!lease.IsAcquired)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "too many requests, slow down"));
        }
    }

    public void Dispose() => limiter.Dispose();
}
