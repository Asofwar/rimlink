using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Colonies;
using Rimlink.Server.Application.Live;

namespace Rimlink.Server.Grpc;

public sealed class ColoniesGrpcService(ColonyService colonies, LiveSessionService live) : Protocol.V1.Colonies.ColoniesBase
{
    public override Task<ColonyAccessList> List(Empty request, ServerCallContext context) => colonies.List(context.GetSession().PlayerId);
    public override Task<ColonyAccess> Invite(ColonyInviteRequest request, ServerCallContext context) => colonies.Invite(context.GetSession().PlayerId, request.MemberPlayerId);
    public override Task<ColonyAccess> Assign(ColonyAssignment request, ServerCallContext context) => colonies.Assign(context.GetSession().PlayerId, request);
    public override async Task<Empty> Answer(ColonyAnswerRequest request, ServerCallContext context)
    {
        await colonies.Answer(context.GetSession().PlayerId, request.OwnerPlayerId, request.Accept);
        return new Empty();
    }
    public override async Task<Empty> Revoke(ColonyAccessRef request, ServerCallContext context)
    {
        await colonies.Revoke(context.GetSession().PlayerId, request.OwnerPlayerId, request.MemberPlayerId);
        return new Empty();
    }
    public override async Task<Protocol.V1.LiveSession> Join(ColonyJoinRequest request, ServerCallContext context) => await live.ToProto(await live.JoinColony(context.GetSession().PlayerId, request));
}
