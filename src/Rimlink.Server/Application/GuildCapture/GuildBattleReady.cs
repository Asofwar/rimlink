using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildAttackerReadyReceipt(int Version,byte[] Intent,string CampaignId,string GrantId,long CommanderId,long GrantEpoch,
    BattleArtifactDescriptor Manifest,string ManifestDigest,DateTime ReadyAtUtc,DateTime DeadlineUtc,string TransitionId);

internal sealed partial class GuildOutpostService
{
    internal async Task<BattleAttackerReadyReply> AttackerReadyAsync(Session session,BattleAttackerReadyRequest request,CancellationToken ct)
    {
        request = GuildAuthorityEvidence.ReadReady(request.ToByteArray());
        var scope = new GuildMutationScope(request.Scope.WorldInstanceId,request.Scope.RestoreBoundary);
        var receipt = await MutateAsync(scope,session.PlayerId,"player:" + session.PlayerId,request.ClientRequestId,"GuildAttackerReady",request,async (lease,id,token) =>
        {
            await RequireCaptureOpenHeldAsync(scope.WorldId, token);
            var battle = await db.Set<GuildBattle>().SingleOrDefaultAsync(b => b.Id == request.Scope.BattleId,token) ?? throw Refused();
            var campaign = await db.Set<GuildCampaign>().SingleAsync(c => c.Id == battle.CampaignId,token);
            var sample = clock.Sample();
            if (!battle.Active || battle.Phase != "Prepared" || battle.StartDigest is not null || !campaign.Active || campaign.Phase != "PreparingBattle"
                || battle.WorldId != scope.WorldId || battle.Boundary != scope.Boundary || battle.Attempt != request.Scope.Attempt || battle.Fence != request.Scope.Fence
                || campaign.AttackerCommanderId != session.PlayerId || sample.Utc < campaign.ScheduledAtUtc || sample.Utc >= campaign.ScheduledAtUtc.AddMinutes(5)) throw Refused();
            await policy.RequireCommanderHeldAsync(lease,session.PlayerId,campaign.AttackerAllianceId,sample.Utc,BattleFighterSide.Attacker,token);
            var proposal = new BattlePreparedJournal(artifacts).LatestHeld(battle.Id,battle.Fence) ?? throw Refused();
            if (proposal.Sequence != request.ProposalSequence || !proposal.ManifestDigest.AsSpan().SequenceEqual(request.ManifestDigest.Span)) throw Refused();
            var manifest = BattleManifestContracts.ReadPrepared(proposal.Manifest);
            var executor = executors.RequireAssignedExecution((await GuildAuthorityEvidence.PreparationAsync(db,battle.Id,token)).RuntimeAssignment);
            executors.RequireCurrent(executor);
            await BattlePreparedAdmission.ValidateHeldAsync(db,manifest,battle,campaign,executor,profiles,artifacts,sessions,token);
            if (!ReferenceEquals(sessions.ByPlayer(session.PlayerId),session)
                || await db.Set<GuildBattleControlGrant>().AnyAsync(g => g.BattleId == battle.Id && g.Side == 1 && g.Epoch >= manifest.Attacker.ControlEpoch,token)) throw Refused();
            foreach (var prior in await db.Set<GuildBattleControlGrant>().Where(g => g.BattleId == battle.Id && g.Side == 1 && g.Active).ToArrayAsync(token)) prior.Active = false;
            var grant = new GuildBattleControlGrant { Id = manifest.Attacker.ControlGrantId,BattleId = battle.Id,PlayerId = session.PlayerId,Side = 1,
                Epoch = manifest.Attacker.ControlEpoch,CapabilityDigest = request.CapabilityDigest.ToByteArray(),ExpiresAtUtc = campaign.ScheduledAtUtc + GuildBattleSchedule.ReservationDuration,Active = true };
            db.Add(grant);
            return new GuildAttackerReadyReceipt(1,request.ToByteArray(),campaign.Id,grant.Id,session.PlayerId,grant.Epoch,proposal.Descriptor,
                Convert.ToHexStringLower(proposal.ManifestDigest),sample.Utc,campaign.ScheduledAtUtc.AddMinutes(5),id);
        },ct,validateScope: async token =>
        {
            BattleCommanderIdentity.RequireStillCurrent(sessions,session);
            if (session.GuildVersion != 1) throw Refused();
            var proposed = new BattlePreparedJournal(artifacts).ReadHeld(request.Scope.BattleId,request.Scope.Fence,request.ProposalSequence);
            var binding = BattleManifestContracts.ReadPrepared(proposed.Manifest).Attacker;
            if (binding.PlayerId != session.PlayerId) throw Refused();
            await BattleCommanderIdentity.RequireAsync(db,sessions,binding,binding.AllianceId,session.PlayerId,request.ProposalSequence,false,token);
        },battleLock:true);
        return new() { GrantId = receipt.GrantId,ControlEpoch = receipt.GrantEpoch,ManifestDigest = ByteString.CopyFrom(Convert.FromHexString(receipt.ManifestDigest)),
            ReadyAtUnixMs = new DateTimeOffset(receipt.ReadyAtUtc).ToUnixTimeMilliseconds(),DeadlineUnixMs = new DateTimeOffset(receipt.DeadlineUtc).ToUnixTimeMilliseconds(),TransitionId = receipt.TransitionId };
    }
}

internal static partial class GuildAuthorityEvidence
{
    internal static BattleAttackerReadyRequest ReadReady(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 1024) throw new InvalidDataException("guild-ready-request-invalid");
        BattleAttackerReadyRequest value;
        try { value = BattleAttackerReadyRequest.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes); }
        catch (InvalidProtocolBufferException) { throw new InvalidDataException("guild-ready-request-invalid"); }
        if (!value.ToByteArray().AsSpan().SequenceEqual(bytes) || !Guid.TryParseExact(value.ClientRequestId,"D",out var request) || request == Guid.Empty
            || value.Scope is not { } scope || !Guid.TryParseExact(scope.BattleId,"D",out var battle) || battle == Guid.Empty
            || !Guid.TryParseExact(scope.RestoreBoundary,"D",out var boundary) || boundary == Guid.Empty || string.IsNullOrWhiteSpace(scope.WorldInstanceId)
            || scope.WorldInstanceId.Length > 256 || scope.WorldInstanceId.Any(char.IsControl) || scope.Attempt <= 0 || scope.Fence <= 0
            || value.ProposalSequence is < 1 or > 8 || value.ManifestDigest.Length != 32 || value.CapabilityDigest.Length != 32) throw new InvalidDataException("guild-ready-request-invalid");
        return value;
    }
    internal static GuildAttackerReadyReceipt ReadReadyReceipt(byte[] bytes) => JsonSerializer.Deserialize<GuildAttackerReadyReceipt>(bytes,Json) ?? throw InvalidReady();
    internal static async Task<GuildAttackerReadyReceipt> ReadyAsync(RimlinkDbContext db,string battleId,int sequence,CancellationToken ct)
    {
        var operations = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildAttackerReady").Select(o => o.Receipt).ToArrayAsync(ct);
        var ready = operations.Select(b => JsonSerializer.Deserialize<GuildAttackerReadyReceipt>(b,Json) ?? throw InvalidReady()).Where(r => r.Manifest.BattleId == battleId && r.GrantEpoch == sequence).ToArray();
        return ready.Length == 1 ? ready[0] : throw InvalidReady();
    }
    private static async Task ValidateReadyAsync(RimlinkDbContext db,BattleArtifactStore artifacts,GuildCaptureOperation[] operations,
        IReadOnlyDictionary<string,OutpostTransition> transitions,CancellationToken ct)
    {
        foreach (var operation in operations.Where(o => o.Kind == "GuildAttackerReady"))
        {
            try
            {
                var receipt = JsonSerializer.Deserialize<GuildAttackerReadyReceipt>(operation.Receipt,Json) ?? throw InvalidReady();
                var request = ReadReady(receipt.Intent);
                var proposal = new BattlePreparedJournal(artifacts).ReadHeld(request.Scope.BattleId,request.Scope.Fence,request.ProposalSequence);
                var manifest = BattleManifestContracts.ReadPrepared(proposal.Manifest);
                if (receipt.Version != 1 || receipt.CommanderId != operation.ActorId || receipt.TransitionId != operation.TransitionId || request.ClientRequestId != operation.RequestId
                    || !SHA256.HashData(receipt.Intent).AsSpan().SequenceEqual(operation.IntentDigest) || receipt.CampaignId != manifest.CampaignId
                    || receipt.CommanderId != manifest.Attacker.PlayerId || receipt.GrantId != manifest.Attacker.ControlGrantId || receipt.GrantEpoch != manifest.Attacker.ControlEpoch
                    || receipt.Manifest != proposal.Descriptor || receipt.ManifestDigest != Convert.ToHexStringLower(proposal.ManifestDigest)
                    || !proposal.ManifestDigest.AsSpan().SequenceEqual(request.ManifestDigest.Span) || request.Scope.WorldInstanceId != manifest.WorldInstanceId
                    || request.Scope.RestoreBoundary != manifest.RestoreBoundary || request.Scope.Attempt != manifest.Attempt
                    || receipt.ReadyAtUtc.Kind != DateTimeKind.Utc || receipt.DeadlineUtc.Kind != DateTimeKind.Utc
                    || receipt.ReadyAtUtc.Ticks < manifest.SlotStartUtcTicks || receipt.ReadyAtUtc >= receipt.DeadlineUtc || receipt.DeadlineUtc.Ticks != manifest.ReadinessDeadlineUtcTicks
                    || !transitions.TryGetValue(receipt.TransitionId,out var transition) || transition.WorldId != manifest.WorldInstanceId || transition.RestoreBoundary != manifest.RestoreBoundary) throw InvalidReady();
                RequireEarlier(transitions,manifest.ApprovalTransitionId,receipt.TransitionId);
                RequireEarlier(transitions,manifest.PreparationTransitionId,receipt.TransitionId);
                var grant = await db.Set<GuildBattleControlGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.Id == receipt.GrantId,ct);
                if (grant is null || grant.BattleId != manifest.BattleId || grant.PlayerId != receipt.CommanderId || grant.Epoch != receipt.GrantEpoch || grant.Side != 1
                    || grant.ExpiresAtUtc.Ticks != manifest.SlotEndUtcTicks || !grant.CapabilityDigest.AsSpan().SequenceEqual(request.CapabilityDigest.Span)) throw InvalidReady();
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or ArgumentException or NullReferenceException)
            { throw new OutpostEvidenceUnavailableException("guild-ready-evidence-invalid: " + error.GetType().Name); }
        }
    }
    private static void RequireEarlier(IReadOnlyDictionary<string,OutpostTransition> transitions,string earlier,string later)
    {
        if (!transitions.TryGetValue(earlier,out var before) || !transitions.TryGetValue(later,out var after) || earlier == later) throw new OutpostEvidenceUnavailableException("guild-transition-order-invalid");
        var byHead = transitions.Values.ToDictionary(t => t.NextHeadId,StringComparer.Ordinal);
        string cursor = after.OldHeadId;
        for (int count = 0;count < transitions.Count;count++)
        {
            if (cursor == before.NextHeadId) return;
            if (!byHead.TryGetValue(cursor,out var previous)) break;
            cursor = previous.OldHeadId;
        }
        throw new OutpostEvidenceUnavailableException("guild-transition-order-invalid");
    }
    private static OutpostEvidenceUnavailableException InvalidReady() => new("guild-ready-evidence-mismatch");
}
