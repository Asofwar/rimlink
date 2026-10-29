using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;

namespace Rimlink.Server.Grpc;

public sealed class LiveGrpcService(LiveSessionService live) : Protocol.V1.Live.LiveBase
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

    public override Task<PlayerFlags> SetAutoAccept(AutoAcceptRequest request, ServerCallContext context) =>
        live.SetAutoAccept(context.GetSession().PlayerId, request.Enabled);

    public override Task Stream(IAsyncStreamReader<LiveClientMsg> requestStream, IServerStreamWriter<LiveServerMsg> responseStream, ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "the frame relay is not available yet"));
}
