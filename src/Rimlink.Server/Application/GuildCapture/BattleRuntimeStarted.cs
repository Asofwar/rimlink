using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleControlTransport
{
    internal async Task<BattleRuntimeStartedReply> RuntimeStartedAsync(BattleRuntimeStartedRequest request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>4096 || request.Executor is null || request.Proof is null)throw Refused();
        var proof=BattleRuntimeStartedContracts.Read(request.Proof.ToByteArray());var executor=BattleResultStaging.Lease(request.Executor);executors.RequireExecutionTransport(executor);
        await using var held=await admission.HoldAsync(ct,GuildRuntimeUse.Simulation);var battle=await RequireBattleAsync(held,proof.Scope,ct);var start=cache.Start;
        var assignment=(await GuildAuthorityEvidence.PreparationAsync(held.Db,battle.Id,ct)).RuntimeAssignment;
        if(executors.RequireAssignedExecution(assignment,allowStartReplay:true)!=executor || proof.RuntimeInstanceId!=start.RuntimeInstanceId || proof.AssignmentTransitionId!=start.PreparationTransitionId
            || !proof.StartDigest.Span.SequenceEqual(battle.StartDigest) || proof.InitialTick!=start.InitialTick || !proof.InitialSnapshot.Equals(start.InitialSnapshot)
            || (await GuildAuthorityEvidence.StartRequestAsync(held.Db,battle.Id,ct)).ClientRequestId!=proof.StartRequestId)throw Refused();
        cache.Journal.BindRuntime(proof);executors.ConfirmRuntimeStarted(executor,proof);
        return new(){Executor=executors.Wire(executor),ProofDigest=ByteString.CopyFrom(SHA256.HashData(proof.ToByteArray())),Position=cache.Journal.Position,
            ElapsedSinceStartMs=Math.Max(0,(long)(clock.Sample().Utc-battle.StartedAtUtc!.Value).TotalMilliseconds)};
    }
}

internal static partial class GuildAuthorityEvidence
{
    internal static void RequireRuntimeStarted(BattleCommandJournal journal,BattlePreparedManifestV1 start,BattleStartRequest request)
    {
        var proof=journal.RuntimeStarted??throw new Outposts.OutpostEvidenceUnavailableException("battle-native-runtime-not-bound");
        if(proof.RuntimeInstanceId!=start.RuntimeInstanceId || proof.AssignmentTransitionId!=start.PreparationTransitionId || proof.StartRequestId!=request.ClientRequestId
            || !proof.Scope.Equals(request.Scope) || !proof.StartDigest.Equals(request.ManifestDigest) || proof.InitialTick!=start.InitialTick || !proof.InitialSnapshot.Equals(start.InitialSnapshot))
            throw new Outposts.OutpostEvidenceUnavailableException("battle-native-runtime-binding-invalid");
    }
    internal static async Task<BattleStartRequest> StartRequestAsync(RimlinkDbContext db,string battleId,CancellationToken ct)
    {
        var bytes=await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o=>o.Kind=="GuildBattleStarted").Select(o=>o.Receipt).ToArrayAsync(ct);
        var matches=bytes.Select(b=>JsonSerializer.Deserialize<GuildBattleStartReceipt>(b,Json)??throw InvalidStart()).Select(r=>ReadStart(r.Intent)).Where(r=>r.Scope.BattleId==battleId).ToArray();
        return matches.Length==1?matches[0]:throw InvalidStart();
    }
}
