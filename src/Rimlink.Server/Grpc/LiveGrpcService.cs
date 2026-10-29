using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;

namespace Rimlink.Server.Grpc;

public sealed class LiveGrpcService(LiveSessionService live, LiveRelay relay, ILogger<LiveGrpcService> log) : Protocol.V1.Live.LiveBase
{
    public override async Task<LiveSession> Propose(LiveProposal request, ServerCallContext context) =>
        await live.ToProto(await live.Propose(context.GetSession().PlayerId, request));

    public override async Task<LiveSession> Respond(LiveAnswer request, ServerCallContext context) =>
        await live.ToProto(await live.Respond(context.GetSession().PlayerId, request.SessionId, request.Accept));

    public override async Task<Empty> Cancel(LiveRef request, ServerCallContext context)
    {
        await live.Cancel(context.GetSession().PlayerId, request.SessionId);
        return new Empty();
    }

    public override async Task<LiveSession> Current(Empty request, ServerCallContext context) =>
        await live.ToProto(await live.Current(context.GetSession().PlayerId));

    public override Task<PawnList> Pawns(LiveRef request, ServerCallContext context) => live.PawnsFor(context.GetSession().PlayerId, request.SessionId);

    public override Task<PlayerFlags> SetAutoAccept(AutoAcceptRequest request, ServerCallContext context) =>
        live.SetAutoAccept(context.GetSession().PlayerId, request.Enabled);

    /// <summary>The first frame must be a Hello naming the session and the role; after that frames are relayed to the peer until End, a close or a lost heartbeat.</summary>
    public override async Task Stream(IAsyncStreamReader<LiveClientMsg> requestStream, IServerStreamWriter<LiveServerMsg> responseStream, ServerCallContext context)
    {
        var session = context.GetSession();
        if (!await requestStream.MoveNext(context.CancellationToken) || requestStream.Current.MCase != LiveClientMsg.MOneofCase.Hello)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "the first frame must be a Hello"));
        var hello = requestStream.Current;
        await live.ForStream(hello.SessionId, session.PlayerId, hello.Hello.Role);
        var channel = relay.Attach(hello.SessionId, session.PlayerId, hello.Hello.Role);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, channel.Closed.Token);
        var ct = linked.Token;

        var writer = Task.Run(async () =>
        {
            try
            {
                while (await channel.Queue.ReadAsync(ct) is { } msg)
                    await responseStream.WriteAsync(msg, ct).WaitAsync(EventsGrpcService.WriteTimeout, ct);
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { log.LogWarning("Live stream of {Player} stalled", session.PlayerName); channel.Closed.Cancel(); }
        }, CancellationToken.None);

        try
        {
            await relay.Forward(channel, hello, hello.CalculateSize());
            while (await requestStream.MoveNext(ct))
            {
                var msg = requestStream.Current;
                if (msg.SessionId != hello.SessionId) continue;
                await relay.Forward(channel, msg, msg.CalculateSize());
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            if (channel.ClosedBy is null) await relay.Detach(channel, "closed");
            linked.Cancel();
            await writer;
        }
        if (channel.ClosedBy == "replaced") throw new RpcException(new Status(StatusCode.Aborted, "replaced by a newer stream"));
    }
}
