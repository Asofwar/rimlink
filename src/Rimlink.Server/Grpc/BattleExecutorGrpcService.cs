using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Saves;

namespace Rimlink.Server.Grpc;

// A successful transport preflight does not grant Product readiness or authorize any financial operation.
internal sealed partial class BattleExecutorGrpcService(BattleExecutorRegistry executors, SaveFiles files, BattleExecutorWorkService work, BattlePreparedAdmission prepared, GuildOutpostService battles, BattleResultStaging results,BattleControlTransport transport,BattleExecutorHelloService hello) : Protocol.V1.BattleExecutor.BattleExecutorBase
{
    public override async Task<BattleCleanupChallengeMessage> CleanupChallenge(BattleCleanupChallengeRequest request, ServerCallContext context)
    {
        if (request.CalculateSize() > 1024 || request.Executor?.ExecutorId != BattleExecutorAuthentication.Identity(context) || request.Scope is null) throw Refused();
        try { return await battles.CleanupChallengeAsync(BattleResultStaging.Lease(request.Executor),request.Scope,context.CancellationToken); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"cleanup challenge is unavailable or requires reconciliation"); }
        catch (InvalidDataException) { throw Refused(); }
        catch (IOException) { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"cleanup evidence storage is unavailable"); }
    }
    public override async Task<BattleCleanupReceipt> Cleanup(BattleCleanupRequest request, ServerCallContext context)
    {
        if (request.CalculateSize() > 4096 || request.Executor?.ExecutorId != BattleExecutorAuthentication.Identity(context) || request.Proof is null) throw Refused();
        try { return await battles.CleanupBattleAsync(BattleResultStaging.Lease(request.Executor),request.ClientRequestId,request.Proof,context.CancellationToken); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"battle cleanup is unavailable or requires reconciliation"); }
        catch (InvalidDataException) { throw Refused(); }
        catch (IOException) { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"cleanup evidence storage is unavailable"); }
    }
    public override async Task<BattleArtifactProgressMessage> UploadResultArtifact(BattleResultArtifactChunk request, ServerCallContext context)
    {
        if (request.Chunk?.Executor?.ExecutorId != BattleExecutorAuthentication.Identity(context)) throw Refused();
        try { return await results.UploadAsync(request,context.CancellationToken); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"result artifact upload is unavailable or requires reconciliation"); }
        catch (InvalidDataException) { throw Refused(); }
        catch (IOException) { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"result artifact storage is unavailable"); }
    }
    public override async Task<BattleFinalReply> Final(BattleFinalRequest request, ServerCallContext context)
    {
        if (request.CalculateSize() > 16384 || request.Executor?.ExecutorId != BattleExecutorAuthentication.Identity(context) || request.Envelope is null) throw Refused();
        try { return await battles.ApplyBattleFinalAsync(BattleResultStaging.Lease(request.Executor),request.Envelope,context.CancellationToken); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException error) when (error.Message == BattleResultStaging.DispatchOutcomeUnresolved)
        { throw RpcErrors.With(StatusCode.Unavailable,ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"battle final is waiting for command evidence"); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException error) when (error.Message == "guild-capture-frozen")
        { throw RpcErrors.With(StatusCode.Unavailable, ErrorCode.GuildUnavailable, RefusalKeys.GuildUnavailable, "battle final is held by the operator"); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"battle final is unavailable or requires reconciliation"); }
        catch (InvalidDataException) { throw Refused(); }
        catch (IOException) { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"battle final storage is unavailable"); }
    }
    public override async Task<BattleStartAuthorization> Start(BattleStartRequest request, ServerCallContext context)
    {
        if (request.Executor?.ExecutorId != BattleExecutorAuthentication.Identity(context)) throw Refused();
        try { return await battles.StartBattleAsync(request,context.CancellationToken); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"battle start is unavailable or requires reconciliation"); }
        catch (InvalidDataException) { throw Refused(); }
        catch (IOException) { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"battle start storage is unavailable"); }
    }
    public override async Task<BattlePreparedProposalReply> ProposePrepared(BattlePreparedProposalRequest request, ServerCallContext context)
    {
        if (request.Executor?.ExecutorId != BattleExecutorAuthentication.Identity(context)) throw Refused();
        try { return await prepared.ProposeAsync(request,context.CancellationToken); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"prepared manifest is unavailable or requires reconciliation"); }
        catch (InvalidDataException) { throw Refused(); }
        catch (IOException) { throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"prepared manifest storage is unavailable"); }
    }
    public override async Task<BattleArtifactProgressMessage> UploadInitial(BattleArtifactChunk request, ServerCallContext context)
    {
        if (request.Executor?.ExecutorId != BattleExecutorAuthentication.Identity(context)) throw Refused();
        try { return await work.UploadInitialAsync(request, context.CancellationToken); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildUnavailable, RefusalKeys.GuildUnavailable, "initial artifact upload is unavailable or requires reconciliation"); }
        catch (InvalidDataException) { throw Refused(); }
        catch (IOException) { throw RpcErrors.Failed(ErrorCode.GuildUnavailable, RefusalKeys.GuildUnavailable, "initial artifact storage is unavailable"); }
    }
    public override async Task<BattleExecutorWorkReply> Poll(BattleExecutorPoll request, ServerCallContext context)
    {
        if (request.CalculateSize() > 1024 || request.Lease is not { } lease || lease.ExecutorId != BattleExecutorAuthentication.Identity(context)) throw Refused();
        try { return await work.PollAsync(new(lease.ExecutorId, lease.LeaseId, lease.Fence, lease.ProfileDigest), context.CancellationToken); }
        catch (Application.Outposts.OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildUnavailable, RefusalKeys.GuildUnavailable, "executor work is unavailable or requires reconciliation"); }
        catch (InvalidDataException) { throw Refused(); }
    }
    public override async Task<BattleExecutorLeaseMessage> Register(BattleExecutorHello request, ServerCallContext context)
    {
        if (request.CalculateSize() > 1024 || request.ExecutorId != BattleExecutorAuthentication.Identity(context)) throw Refused();
        using var storage = await files.PerStoreLock.HoldAsync(context.CancellationToken);
        try { return Wire(executors.RegisterAuthenticated(request.ExecutorId, request.ProfileDigest.ToByteArray())); }
        catch (InvalidDataException) { throw Refused(); }
    }
    public override async Task<BattleExecutorLeaseMessage> Heartbeat(BattleExecutorHeartbeat request, ServerCallContext context)
    {
        if (request.CalculateSize() > 1024 || request.Lease is not { } lease || lease.ExecutorId != BattleExecutorAuthentication.Identity(context)) throw Refused();
        using var storage = await files.PerStoreLock.HoldAsync(context.CancellationToken);
        var bound = new BattleExecutorLease(lease.ExecutorId, lease.LeaseId, lease.Fence, lease.ProfileDigest);
        try { executors.HeartbeatAuthenticated(bound, request.HasUnfinishedProcess); return Wire(bound); }
        catch (InvalidDataException) { throw Refused(); }
    }
    private BattleExecutorLeaseMessage Wire(BattleExecutorLease lease)=>executors.BindingOf(lease) is null
        ?new(){ExecutorId=lease.ExecutorId,LeaseId=lease.LeaseId,Fence=lease.Fence,ProfileDigest=lease.ProfileDigest}:executors.Wire(lease);
    private static RpcException Refused() => RpcErrors.Failed(ErrorCode.BattleExecutorRefused, RefusalKeys.GuildExecutor, "executor identity, profile or lease refused");
}
