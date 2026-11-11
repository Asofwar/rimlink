using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildBattleStartReceipt(int Version,byte[] Intent,string ReadyTransitionId,string InitialArtifactId,string StartArtifactId,
    DateTime StartedAtUtc,string TransitionId);

internal sealed partial class GuildOutpostService
{
    internal async Task<BattleStartAuthorization> StartBattleAsync(BattleStartRequest request,CancellationToken ct)
    {
        request = GuildAuthorityEvidence.ReadStart(request.ToByteArray());
        var scope = new GuildMutationScope(request.Scope.WorldInstanceId,request.Scope.RestoreBoundary);
        var executor = new BattleExecutorLease(request.Executor.ExecutorId,request.Executor.LeaseId,request.Executor.Fence,request.Executor.ProfileDigest);
        var observed = await db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == request.Scope.BattleId,ct) ?? throw Refused();
        var observedCampaign = await db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c => c.Id == observed.CampaignId,ct);
        var observedLeader = await db.Set<AllianceGroup>().AsNoTracking().SingleAsync(g => g.Id == observedCampaign.AttackerAllianceId,ct);
        var assignment=(await GuildAuthorityEvidence.PreparationAsync(db,observed.Id,ct)).RuntimeAssignment;
        var intent=request.Clone();intent.Executor=new(){ExecutorId=assignment.ExecutorId,LeaseId=assignment.OriginalLeaseId,Fence=assignment.OriginalLeaseFence,
            ProfileDigest=ByteString.CopyFrom(Convert.FromHexString(assignment.ProfileDigest))};
        bool replayOnly=executors.Wire(executor).StartReplayOnly;
        if(replayOnly)
        {
            var sourceRequest=await GuildAuthorityEvidence.StartRequestAsync(db,observed.Id,ct);
            if(!sourceRequest.Equals(intent))throw Refused();
        }
        var receipt = await MutateAsync(scope,0,"executor:" + executor.ExecutorId,request.ClientRequestId,"GuildBattleStarted",intent,async (lease,id,token) =>
        {
            await RequireCaptureOpenHeldAsync(scope.WorldId, token);
            var battle = await db.Set<GuildBattle>().SingleAsync(b => b.Id == request.Scope.BattleId,token);
            var campaign = await db.Set<GuildCampaign>().SingleAsync(c => c.Id == battle.CampaignId,token);
            var alliance = await db.Set<AllianceGroup>().AsNoTracking().SingleAsync(g => g.Id == campaign.AttackerAllianceId,token);
            var sample = clock.Sample();
            if (!battle.Active || battle.Phase != "Prepared" || battle.StartDigest is not null || !campaign.Active || campaign.Phase != "PreparingBattle"
                || battle.WorldId != scope.WorldId || battle.Boundary != scope.Boundary || battle.Attempt != request.Scope.Attempt || battle.Fence != request.Scope.Fence
                || battle.ExecutorId != executor.ExecutorId || sample.Utc < campaign.ScheduledAtUtc || sample.Utc >= campaign.ScheduledAtUtc.AddMinutes(5)
                || !lease.CoversOwner(alliance.OwnerId) || !lease.CoversOwner(campaign.AttackerCommanderId)
                || await db.Set<GuildBattle>().AnyAsync(b => b.Active && b.Id != battle.Id,token)) throw Refused();
            await policy.RequireLeaderHeldAsync(lease,alliance.OwnerId,alliance.Id,sample.Utc,token);
            await policy.RequireCommanderHeldAsync(lease,campaign.AttackerCommanderId,alliance.Id,sample.Utc,BattleFighterSide.Attacker,token);
            var proposal = new BattlePreparedJournal(artifacts).LatestHeld(battle.Id,battle.Fence) ?? throw Refused();
            if (!proposal.ManifestDigest.AsSpan().SequenceEqual(request.ManifestDigest.Span)) throw Refused();
            var manifest = BattleManifestContracts.ReadPrepared(proposal.Manifest);
            await BattlePreparedAdmission.ValidateHeldAsync(db,manifest,battle,campaign,executor,profiles,artifacts,sessions,token,requireDefenderSession:false);
            var ready = await GuildAuthorityEvidence.ReadyAsync(db,battle.Id,proposal.Sequence,token);
            var grant = await db.Set<GuildBattleControlGrant>().AsNoTracking().SingleAsync(g => g.Id == ready.GrantId,token);
            if (ready.ManifestDigest != Convert.ToHexStringLower(proposal.ManifestDigest) || ready.DeadlineUtc != campaign.ScheduledAtUtc.AddMinutes(5)
                || !grant.Active || grant.ExpiresAtUtc <= sample.Utc || grant.PlayerId != campaign.AttackerCommanderId || grant.Side != 1
                || grant.Epoch != manifest.Attacker.ControlEpoch) throw Refused();
            executors.RequireCurrent(executor);
            string initialId = Guid.NewGuid().ToString("D"),startId = Guid.NewGuid().ToString("D");
            var initial = new BattleArtifactDescriptor(1,battle.Id,battle.WorldId,battle.Boundary,battle.ExecutorId,battle.Fence,"initial",
                manifest.InitialSnapshot.SizeBytes,Convert.ToHexStringLower(manifest.InitialSnapshot.Sha256.Span));
            var initialProof = artifacts.ValidateCompleteHeld(initial);
            db.Add(new GuildBattleArtifact { Id = initialId,BattleId = battle.Id,Kind = "initial",Digest = manifest.InitialSnapshot.Sha256.ToByteArray(),
                SizeBytes = initial.SizeBytes,RelativePath = initialProof.RelativePath });
            db.Add(new GuildBattleArtifact { Id = startId,BattleId = battle.Id,Kind = "start",Digest = Convert.FromHexString(proposal.Descriptor.Sha256),
                SizeBytes = proposal.Descriptor.SizeBytes,RelativePath = proposal.Artifact.RelativePath });
            battle.StartManifest = proposal.Manifest.ToArray(); battle.StartDigest = proposal.ManifestDigest.ToArray(); battle.StartedAtUtc = sample.Utc;
            battle.Phase = "StartCommitted"; campaign.Phase = "StartCommitted";
            var row = await db.Set<GuildOutpostRow>().SingleAsync(o => o.Id == campaign.OutpostId,token); row.Phase = "Battle";
            return new GuildBattleStartReceipt(1,intent.ToByteArray(),ready.TransitionId,initialId,startId,sample.Utc,id);
        },ct,[observedLeader.OwnerId,observedCampaign.AttackerCommanderId],validateScope: _ =>
        { if(executors.RequireAssignedExecution(assignment,allowStartReplay:replayOnly)!=executor)throw Refused(); return Task.CompletedTask; },battleLock:true);
        var source = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == receipt.TransitionId,ct) ?? throw Refused();
        var committed = await db.Set<GuildBattle>().AsNoTracking().SingleAsync(b => b.Id == request.Scope.BattleId,ct);
        if (committed.StartManifest is null || committed.StartDigest is null) throw Refused();
        return new() { Scope = request.Scope.Clone(),Manifest = ByteString.CopyFrom(committed.StartManifest),ManifestDigest = ByteString.CopyFrom(committed.StartDigest),
            StartedAtUnixMs = new DateTimeOffset(receipt.StartedAtUtc).ToUnixTimeMilliseconds(),TransitionId = receipt.TransitionId,ReadyTransitionId = receipt.ReadyTransitionId,
            AuthorityHeadId = source.NextHeadId,AuthorityHeadDigest = ByteString.CopyFrom(source.Digest) };
    }
}

internal static partial class GuildAuthorityEvidence
{
    internal static BattleStartRequest ReadStart(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 1024) throw new InvalidDataException("guild-start-request-invalid");
        BattleStartRequest value;
        try { value = BattleStartRequest.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes); }
        catch (InvalidProtocolBufferException) { throw new InvalidDataException("guild-start-request-invalid"); }
        if (!value.ToByteArray().AsSpan().SequenceEqual(bytes) || !Guid.TryParseExact(value.ClientRequestId,"D",out var request) || request == Guid.Empty
            || value.Scope is not { } scope || !Guid.TryParseExact(scope.BattleId,"D",out var battle) || battle == Guid.Empty
            || !Guid.TryParseExact(scope.RestoreBoundary,"D",out var boundary) || boundary == Guid.Empty || string.IsNullOrWhiteSpace(scope.WorldInstanceId)
            || scope.WorldInstanceId.Length > 256 || scope.WorldInstanceId.Any(char.IsControl) || scope.Attempt <= 0 || scope.Fence <= 0 || value.ManifestDigest.Length != 32
            || value.Executor is not { } executor || string.IsNullOrWhiteSpace(executor.ExecutorId) || executor.ExecutorId.Length > 128
            || !Guid.TryParseExact(executor.LeaseId,"D",out var lease) || lease == Guid.Empty || executor.Fence <= 0 || executor.ProfileDigest.Length != 32) throw new InvalidDataException("guild-start-request-invalid");
        return value;
    }
    private static async Task ValidateStartsAsync(RimlinkDbContext db,BattleArtifactStore artifacts,GuildCaptureOperation[] operations,
        IReadOnlyDictionary<string,OutpostTransition> transitions,CancellationToken ct)
    {
        foreach (var operation in operations.Where(o => o.Kind == "GuildBattleStarted"))
        {
            try
            {
                var receipt = JsonSerializer.Deserialize<GuildBattleStartReceipt>(operation.Receipt,Json) ?? throw InvalidStart();
                var request = ReadStart(receipt.Intent);
                var battle = await db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == request.Scope.BattleId,ct);
                if (receipt.Version != 1 || operation.ActorId != 0 || operation.RequestId != request.ClientRequestId || receipt.TransitionId != operation.TransitionId
                    || !SHA256.HashData(receipt.Intent).AsSpan().SequenceEqual(operation.IntentDigest) || battle?.StartManifest is null || battle.StartDigest is null
                    || receipt.StartedAtUtc.Kind != DateTimeKind.Utc || battle.StartedAtUtc != receipt.StartedAtUtc || battle.Fence < request.Scope.Fence
                    || !transitions.TryGetValue(receipt.TransitionId,out var transition) || transition.WorldId != request.Scope.WorldInstanceId
                    || transition.RestoreBoundary != request.Scope.RestoreBoundary || transition.Actor != "executor:" + request.Executor.ExecutorId) throw InvalidStart();
                var manifest = BattleManifestContracts.ReadPrepared(battle.StartManifest);
                var proposal = new BattlePreparedJournal(artifacts).ReadHeld(battle.Id,manifest.Fence,manifest.ProposalSequence);
                if (!proposal.Manifest.AsSpan().SequenceEqual(battle.StartManifest) || !proposal.ManifestDigest.AsSpan().SequenceEqual(battle.StartDigest)
                    || !request.ManifestDigest.Span.SequenceEqual(battle.StartDigest) || request.Scope.Attempt != manifest.Attempt || request.Scope.Fence != manifest.Fence
                    || request.Scope.WorldInstanceId != manifest.WorldInstanceId || request.Scope.RestoreBoundary != manifest.RestoreBoundary
                    || request.Executor.ExecutorId != manifest.ExecutorId || request.Executor.LeaseId != manifest.ExecutorLeaseId || request.Executor.Fence != manifest.ExecutorLeaseFence
                    || !request.Executor.ProfileDigest.Equals(manifest.ProfileDigest) || receipt.StartedAtUtc.Ticks < manifest.SlotStartUtcTicks
                    || receipt.StartedAtUtc.Ticks >= manifest.ReadinessDeadlineUtcTicks) throw InvalidStart();
                var ready = await ReadyAsync(db,battle.Id,manifest.ProposalSequence,ct);
                if (receipt.ReadyTransitionId != ready.TransitionId || ready.ManifestDigest != Convert.ToHexStringLower(battle.StartDigest)) throw InvalidStart();
                RequireEarlier(transitions,ready.TransitionId,receipt.TransitionId);
                var initial = artifacts.ValidateCompleteHeld(new(1,battle.Id,manifest.WorldInstanceId,manifest.RestoreBoundary,manifest.ExecutorId,manifest.Fence,"initial",
                    manifest.InitialSnapshot.SizeBytes,Convert.ToHexStringLower(manifest.InitialSnapshot.Sha256.Span)));
                await Artifact(receipt.InitialArtifactId,"initial",initial,manifest.InitialSnapshot.Sha256.ToByteArray());
                await Artifact(receipt.StartArtifactId,"start",proposal.Artifact,Convert.FromHexString(proposal.Descriptor.Sha256));
                async Task Artifact(string id,string kind,BattleArtifactProof proof,byte[] hash)
                {
                    var row = await db.Set<GuildBattleArtifact>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id,ct);
                    if (row is null || row.BattleId != battle.Id || row.Kind != kind || row.RelativePath != proof.RelativePath || row.SizeBytes != proof.SizeBytes
                        || !row.Digest.AsSpan().SequenceEqual(hash)) throw InvalidStart();
                }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or ArgumentException or NullReferenceException)
            { throw new OutpostEvidenceUnavailableException("guild-start-evidence-invalid: " + error.GetType().Name); }
        }
    }
    private static OutpostEvidenceUnavailableException InvalidStart() => new("guild-start-evidence-mismatch");
}
