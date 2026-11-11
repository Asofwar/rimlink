using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record StagedGuildFinal(BattleFinalEnvelope Envelope,BattleFinalManifestV1 Final,BattleArtifactDescriptor Artifact);

// Cold delivery may use a new transport lease; the original execution identity remains inside the immutable envelope.
internal sealed class BattleResultStaging(BattleExecutorRegistry executors,BattleTransportAdmission admission,BattleArtifactStore artifacts)
{
    internal const string DispatchOutcomeUnresolved = "battle-dispatch-outcome-unresolved";

    internal async Task<BattleArtifactProgressMessage> UploadAsync(BattleResultArtifactChunk request,CancellationToken ct)
    {
        if (request is null || request.CalculateSize() > BattleArtifactStore.ChunkBytes + 16384 || request.Kind is not ("final" or "simulation" or "view")
            || request.Chunk is not { } chunk || request.CalculateSize() - chunk.Data.Length > 16384 || chunk.Executor is null || chunk.Scope is null
            || chunk.Data.Length is < 1 or > BattleArtifactStore.ChunkBytes || chunk.TotalBytes is < 1 or > BattleArtifactStore.MaximumBytes
            || request.Kind == "view" && chunk.TotalBytes > 1048576 || chunk.Sha256.Length != 32 || chunk.Offset < 0 || chunk.Offset > chunk.TotalBytes - chunk.Data.Length)
            throw new InvalidDataException("guild-final-artifact-refused");
        request = request.Clone(); chunk = request.Chunk;
        var executor = Lease(chunk.Executor); executors.RequireCurrent(executor);
        await using var held = await admission.HoldAsync(ct);
        var (battle,_) = await RequireLaneAsync(held,executor,chunk.Scope,ct);
        var descriptor = new BattleArtifactDescriptor(1,battle.Id,battle.WorldId,battle.Boundary,battle.ExecutorId,battle.Fence,request.Kind,
            chunk.TotalBytes,Convert.ToHexStringLower(chunk.Sha256.Span));
        var progress = artifacts.AppendHeld(descriptor,chunk.Offset,chunk.Data.Span);
        var reply = new BattleArtifactProgressMessage { ReceivedBytes = progress.ReceivedBytes,Complete = progress.Complete };
        if (progress.Complete) reply.Artifact = new() { Scope = chunk.Scope.Clone(),Kind = request.Kind,Sha256 = chunk.Sha256,SizeBytes = chunk.TotalBytes };
        return reply;
    }
    internal async Task<StagedGuildFinal> StageAsync(BattleExecutorLease executor,BattleFinalEnvelope envelope,CancellationToken ct)
    {
        envelope = BattleManifestContracts.ReadFinalEnvelope(envelope.ToByteArray());
        if (envelope.Manifest.Length > BattleManifestContracts.FinalBytes) throw Refused();
        BattleFinalManifestV1 candidate;
        try { candidate = BattleFinalManifestV1.Parser.ParseFrom(envelope.Manifest); }
        catch (InvalidProtocolBufferException) { throw new InvalidDataException("guild-final-envelope-invalid"); }
        var scope = new BattleScopeMessage { WorldInstanceId = candidate.WorldInstanceId,RestoreBoundary = candidate.RestoreBoundary,BattleId = candidate.BattleId,
            Attempt = candidate.Attempt,Fence = candidate.Fence };
        executors.RequireCurrent(executor);
        await using var held = await admission.HoldAsync(ct);
        var (battle,start) = await RequireLaneAsync(held,executor,scope,ct);
        var final = BattleManifestContracts.ReadFinal(envelope.Manifest.Span,start);
        ValidateArtifactsHeld(artifacts,final);
        byte[] bytes = envelope.ToByteArray();
        var descriptor = new BattleArtifactDescriptor(1,battle.Id,battle.WorldId,battle.Boundary,battle.ExecutorId,battle.Fence,"result",bytes.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        executors.RequireCurrent(executor);
        artifacts.AppendHeld(descriptor,0,bytes);
        var journal=BattleCommandJournal.OpenHeld(artifacts.CommandDirectoryHeld(battle.Id,battle.Fence),new(battle.Id,battle.WorldId,battle.Boundary,battle.ExecutorId,battle.Fence,battle.StartDigest!));
        // The immutable result closes new dispatch first; old outcomes drain before attempting an external financial pending.
        if(journal.OutstandingDispatch() is not null || journal.CutPending(1) || journal.CutPending(2))
            throw new OutpostEvidenceUnavailableException(DispatchOutcomeUnresolved);
        return new(envelope,final,descriptor);
    }
    internal static void ValidateArtifactsHeld(BattleArtifactStore artifacts,BattleFinalManifestV1 final)
    {
        foreach (var (kind,document) in new[] { ("initial",final.InitialSnapshot),("final",final.FinalSnapshot),("simulation",final.SimulationJournal),("view",final.TerminalView) })
        {
            var descriptor = Descriptor(final,kind,document);
            artifacts.ValidateCompleteHeld(descriptor);
            if (kind == "view") BattleManifestContracts.ReadTerminalView(artifacts.ReadDocumentHeld(descriptor,1048576),final);
        }
    }
    internal static BattleArtifactDescriptor Descriptor(BattleFinalManifestV1 final,string kind,BattleDocumentDescriptor document) =>
        new(1,final.BattleId,final.WorldInstanceId,final.RestoreBoundary,final.ExecutorId,final.Fence,kind,document.SizeBytes,Convert.ToHexStringLower(document.Sha256.Span));
    private async Task<(GuildBattle Battle,BattlePreparedManifestV1 Start)> RequireLaneAsync(BattleTransportAdmission.Lease held,
        BattleExecutorLease executor,BattleScopeMessage scope,CancellationToken ct)
    {
        var battle = await held.Db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == scope.BattleId,ct) ?? throw Refused();
        if (scope.WorldInstanceId != held.Cut.WorldId || scope.RestoreBoundary != held.Cut.Boundary || battle.WorldId != scope.WorldInstanceId
            || battle.Boundary != scope.RestoreBoundary || battle.Attempt != scope.Attempt || battle.Fence != scope.Fence || battle.ExecutorId != executor.ExecutorId
            || battle.StartManifest is null || battle.StartDigest is null || battle.StartedAtUtc is null
            || battle.FinalDigest is null && (!battle.Active || battle.Phase is not ("StartCommitted" or "AwaitingResult"))) throw Refused();
        var start = BattleManifestContracts.ReadPrepared(battle.StartManifest);
        if (start.WorldInstanceId != battle.WorldId || start.RestoreBoundary != battle.Boundary || start.BattleId != battle.Id || start.Fence != battle.Fence
            || start.Attempt != battle.Attempt || !start.ProfileDigest.Equals(executor.ProfileDigest)) throw Refused();
        var binding=executors.BindingOf(executor)??throw Refused();
        if(binding.WorldInstanceId!=battle.WorldId || binding.RestoreBoundary!=battle.Boundary || !binding.ProfileDigest.Equals(start.ProfileDigest)
            || !binding.InventorySha256.Equals(start.MeasuredInventory.Sha256))throw Refused();
        if(binding.Lane==BattleExecutorLane.Execution)
        {if(binding.RuntimeInstanceId!=start.RuntimeInstanceId)throw Refused();}
        else if(binding.Lane!=BattleExecutorLane.RecoveryDelivery || binding.BattleId!=battle.Id || binding.Attempt!=battle.Attempt || binding.Fence!=battle.Fence
            || binding.AssignmentTransitionId!=start.PreparationTransitionId || !binding.StartDigest.Span.SequenceEqual(battle.StartDigest))throw Refused();
        executors.RequireCurrent(executor);
        return (battle,start);
    }
    internal static BattleExecutorLease Lease(BattleExecutorLeaseMessage value) => new(value.ExecutorId,value.LeaseId,value.Fence,value.ProfileDigest);
    private static OutpostEvidenceUnavailableException Refused() => new("guild-final-lane-reconciliation-required");
}
