using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;

namespace Rimlink.Server.Grpc;

public sealed class EventsGrpcService(SessionRegistry sessions, ILogger<EventsGrpcService> log) : Protocol.V1.Events.EventsBase
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
            // The client went away without logging out: the event stream is the connection's heartbeat.
            if (sessions.TryGet(session.Token, out _))
            {
                log.LogInformation("{Player} disconnected", session.PlayerName);
                sessions.End(session.Token);
            }
            return;
        }

        log.LogDebug("Event stream of {Player} ended: session closed", session.PlayerName);
        throw new RpcException(new Status(StatusCode.Unavailable, "session ended"));
    }
}
