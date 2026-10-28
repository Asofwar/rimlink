using Grpc.Core;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Grpc;

public sealed class EventsGrpcService(ILogger<EventsGrpcService> log) : Protocol.V1.Events.EventsBase
{
    public override async Task Subscribe(Empty request, IServerStreamWriter<ServerEvent> responseStream, ServerCallContext context)
    {
        var session = context.GetSession();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, session.Cts.Token);

        try
        {
            await foreach (var ev in session.Events.ReadAll(linked.Token))
            {
                await responseStream.WriteAsync(ev, linked.Token);
            }
        }
        catch (OperationCanceledException) when (session.Cts.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            log.LogDebug("Event stream of {Player} ended: session closed", session.PlayerName);
            throw new RpcException(new Status(StatusCode.Unavailable, "session ended"));
        }
        catch (OperationCanceledException)
        {
            // Client went away; nothing to report.
        }
    }
}
