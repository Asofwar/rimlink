using Rimlink.Client.Economy;
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
    private static DateTime Until => DateTime.UtcNow + EconomyClient.Deadline;
    public Task<OutpostStateReply> State(OutpostStateQuery request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.StateAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Prepare(OutpostPrepareRequest request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.PrepareAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Commit(OutpostProofRequest request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.CommitAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Cancel(OutpostCancelRequest request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.CancelAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Mutate(OutpostMutationRequest request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.MutateAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostOperationReply> Operation(OutpostOperationQuery request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.OperationAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
    public Task<OutpostDeliveryReply> BeginDelivery(OutpostDeliveryRequest request, CancellationToken ct)
    { var r = request.Clone(); return Call(r.Scope, () => connection.OutpostsApi.BeginDeliveryAsync(r, deadline: Until, cancellationToken: ct).ResponseAsync, ct); }
}
