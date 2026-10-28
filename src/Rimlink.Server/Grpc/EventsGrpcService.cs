using Grpc.Core;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Grpc;

public sealed class EventsGrpcService(ILogger<EventsGrpcService> log) : Protocol.V1.Events.EventsBase
{
    public override async Task Subscribe(Empty request, IServerStreamWriter<ServerEvent> responseStream, ServerCallContext context)
    {
        var session = context.GetSession();

        try
        {
            // Reading uses only the call token: when the session ends, its queue is completed and the
            // events already queued (e.g. SessionReplaced) still reach the client before the stream closes.
            await foreach (var ev in session.Events.ReadAll(context.CancellationToken))
            {
                await responseStream.WriteAsync(ev, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return; // client went away
        }

        log.LogDebug("Event stream of {Player} ended: session closed", session.PlayerName);
        throw new RpcException(new Status(StatusCode.Unavailable, "session ended"));
    }
}
