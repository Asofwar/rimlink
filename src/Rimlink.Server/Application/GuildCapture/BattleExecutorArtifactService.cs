using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleExecutorWorkService
{
    internal async Task<BattleArtifactProgressMessage> UploadInitialAsync(BattleArtifactChunk request, CancellationToken ct)
    {
        if (request is null || request.CalculateSize() > BattleArtifactStore.ChunkBytes + 16384 || request.CalculateSize() - request.Data.Length > 16384
            || request.Data.Length is < 1 or > BattleArtifactStore.ChunkBytes || request.Sha256.Length != 32
            || request.TotalBytes is < 1 or > BattleArtifactStore.MaximumBytes || request.Offset < 0 || request.Offset > request.TotalBytes - request.Data.Length
            || request.Executor is null || request.Scope is not { } scope || !Guid.TryParseExact(scope.BattleId, "D", out var id) || id == Guid.Empty
            || !Guid.TryParseExact(scope.RestoreBoundary, "D", out var boundary) || boundary == Guid.Empty
            || scope.Attempt <= 0 || scope.Fence <= 0) throw new InvalidDataException("battle-initial-chunk-refused");
        request = request.Clone(); scope = request.Scope;
        var executor = new BattleExecutorLease(request.Executor.ExecutorId, request.Executor.LeaseId, request.Executor.Fence, request.Executor.ProfileDigest);
        executors.RequireCurrent(executor);
        await using var held = await admission.HoldAsync(ct);
        var (battle, _) = await RequirePreparingAsync(held, executor, scope.BattleId, ct);
        if (scope.WorldInstanceId != battle.WorldId || scope.RestoreBoundary != battle.Boundary || scope.Fence != battle.Fence || scope.Attempt != battle.Attempt) throw Refused();
        var descriptor = new BattleArtifactDescriptor(1, battle.Id, battle.WorldId, battle.Boundary, battle.ExecutorId, battle.Fence,
            "initial", request.TotalBytes, Convert.ToHexStringLower(request.Sha256.Span));
        var progress = artifacts.AppendHeld(descriptor, request.Offset, request.Data.Span);
        var reply = new BattleArtifactProgressMessage { ReceivedBytes = progress.ReceivedBytes, Complete = progress.Complete };
        if (progress.Complete) reply.Artifact = new() { Scope = scope.Clone(), Sha256 = ByteString.CopyFrom(request.Sha256.Span),
            SizeBytes = request.TotalBytes, Kind = "initial" };
        return reply;
    }
}
