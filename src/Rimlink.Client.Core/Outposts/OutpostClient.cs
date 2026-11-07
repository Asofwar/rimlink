using Google.Protobuf;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

/// <summary>Authenticated typed RPCs; a reply or preview alone never authorizes game-object materialization.</summary>
public sealed class OutpostClient(RimlinkConnection connection)
{
    private OutpostScopeMessage? scope;
    public OutpostScopeMessage? Scope => scope?.Clone();
    public void BindScope(OutpostScopeMessage current)
    {
        if (!OutpostReconciliation.ValidScope(current)) throw new InvalidDataException("invalid outpost client scope");
        scope = current.Clone();
    }
    internal bool Matches(OutpostScopeMessage value, LoginReply login) => OutpostReconciliation.ValidScope(value)
        && login.OutpostVersion == 1 && value.OwnerPlayerId == login.Player.PlayerId
        && value.SaveGeneration == login.SaveGeneration && value.SaveWriteEpoch == login.SaveWriteEpoch
        && value.RestoreBoundary == login.OutpostRestoreBoundary
        && (scope is null || OutpostReconciliation.SameScope(scope, value));
    private Task<T> Call<T>(OutpostScopeMessage value, Func<Task<T>> send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var login = connection.Login;
        if (login is null || !Matches(value, login)) throw new InvalidDataException("outpost call belongs to another authentication boundary");
        return connection.ForAuthentication(login, send);
    }
    private async Task<OutpostOperationReply> CallOperation(OutpostScopeMessage value, string id, ByteString digest,
        Func<Task<OutpostOperationReply>> send, CancellationToken ct)
    {
        var reply = await Call(value, send, ct);
        if (reply.ClientRequestId != id || !reply.IntentDigest.Equals(digest) || !OutpostReconciliation.SameScope(reply.Scope, value))
            throw new InvalidDataException("operation reply changed the durable intent");
        return reply;
    }
    public Task<OrdinaryOperationReply> OrdinaryOperation(OrdinaryOperationQuery request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Operation.Scope, () => connection.OutpostsApi.OrdinaryOperationAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OrdinaryOperationReply> BeginOrdinaryReturn(OrdinaryCancelRequest request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.BeginOrdinaryReturnAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    // Protected calls can validate nested restore evidence under the authority lock.
    private static DateTime Until => DateTime.UtcNow.AddMinutes(2);
    public Task<OutpostStateReply> State(OutpostStateQuery request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.StateAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Prepare(OutpostPrepareRequest request, CancellationToken ct)
    { var r = request.Clone(); return CallOperation(r.Scope, r.ClientRequestId, r.IntentDigest, () => connection.OutpostsApi.PrepareAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Commit(OutpostProofRequest request, CancellationToken ct)
    { var r = request.Clone(); return CallOperation(r.Scope, r.ClientRequestId, r.IntentDigest, () => connection.OutpostsApi.CommitAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Cancel(OutpostCancelRequest request, CancellationToken ct)
    { var r = request.Clone(); return CallOperation(r.Scope, r.ClientRequestId, r.IntentDigest, () => connection.OutpostsApi.CancelAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Mutate(OutpostMutationRequest request, CancellationToken ct)
    { var r = request.Clone(); return CallOperation(r.Scope, r.ClientRequestId, r.IntentDigest, () => connection.OutpostsApi.MutateAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Operation(OutpostOperationQuery request, CancellationToken ct)
    { var r = request.Clone(); return CallOperation(r.Scope, r.ClientRequestId, r.IntentDigest, () => connection.OutpostsApi.OperationAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostDeliveryReply> BeginDelivery(OutpostDeliveryRequest request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.BeginDeliveryAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
}
