using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.OutpostAcceptanceHost;

// Dev composition only: after the real service commits, before gRPC receives the response to serialize.
internal sealed class AcceptanceReplyInterceptor(AcceptanceRuntime runtime, OutpostFeature feature) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var observed = request switch
        {
            OutpostPrepareRequest r when context.Method == "/rimlink.v1.Outposts/Prepare" => (r.Scope, r.ClientRequestId, r.IntentDigest, Entry: "prepare-received-before-service", Durable: "prepared-durable"),
            OutpostProofRequest r when context.Method == "/rimlink.v1.Outposts/Commit" => (r.Scope, r.ClientRequestId, r.IntentDigest, Entry: "commit-received-before-service", Durable: "committed-durable"),
            _ => default
        };
        if (observed.Scope is null) return await continuation(request, context);
        var session = context.GetSession();
        var target = new AcceptanceFaultTarget(AcceptanceRuntime.SessionHash(session.Token),
            Convert.ToHexStringLower(SHA256.HashData(observed.Scope.ToByteArray())), observed.ClientRequestId, "staff", Convert.ToHexStringLower(observed.IntentDigest.Span));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, session.Cts.Token);
        await Pause(observed.Entry, "RequestReceivedBeforeService", null);
        var response = await continuation(request, context);
        if (response is OutpostOperationReply reply && reply.Status == (observed.Durable == "prepared-durable" ? OutpostOperationStatus.Prepared : OutpostOperationStatus.Committed))
            await Pause(observed.Durable, "ReplyDurableBeforeTransport", Convert.ToHexStringLower(SHA256.HashData(reply.ToByteArray())));
        return response;

        async Task Pause(string boundary, string kind, string? responseHash)
        {
            if (runtime.Replies.Observe() is not { State: "Armed" } armed || armed.Boundary != boundary) return;
            OutpostRefusalKeys.RequireReady(session, feature); runtime.RequireSession(target);
            var wait = runtime.Replies.ReachAsync(boundary, target, stop.Token);
            if (runtime.Replies.Observe() is { State: "Reached", Target: var held } && held == target)
                runtime.RecordRpc(kind, new { Boundary = boundary, Target = target, AttemptId = Guid.NewGuid().ToString("D"), ResponseSha256 = responseHash });
            bool? drop = await wait;
            if (drop.HasValue)
            {
                runtime.RequireSession(target); stop.Token.ThrowIfCancellationRequested();
                if (drop.Value) throw new RpcException(new Status(StatusCode.Unavailable, "isolated acceptance: RPC interrupted at " + boundary));
            }
        }
    }
}
