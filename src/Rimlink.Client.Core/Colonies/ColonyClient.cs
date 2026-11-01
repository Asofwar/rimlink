using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Colonies;

public sealed class ColonyClient(RimlinkConnection connection)
{
    private static DateTime Until => DateTime.UtcNow.AddSeconds(30);
    public Task<ColonyAccessList> List(CancellationToken ct) => connection.ColoniesApi.ListAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<ColonyAccess> Invite(long member, CancellationToken ct) => connection.ColoniesApi.InviteAsync(new ColonyInviteRequest { MemberPlayerId = member }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Empty> Answer(long owner, bool accept, CancellationToken ct) => connection.ColoniesApi.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = owner, Accept = accept }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<ColonyAccess> Assign(long member, IEnumerable<long> pawnIds, CancellationToken ct) => connection.ColoniesApi.AssignAsync(new ColonyAssignment { MemberPlayerId = member, PawnIds = { pawnIds } }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Empty> Revoke(long owner, long member, CancellationToken ct) => connection.ColoniesApi.RevokeAsync(new ColonyAccessRef { OwnerPlayerId = owner, MemberPlayerId = member }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<LiveSession> Join(long owner, string requestId, CancellationToken ct) => connection.ColoniesApi.JoinAsync(new ColonyJoinRequest { OwnerPlayerId = owner, ClientRequestId = requestId }, deadline: Until, cancellationToken: ct).ResponseAsync;
}
