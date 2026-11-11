using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

// Mirror reads use S/B only. The committed Start stays fixed across viewer reconnects.
internal sealed class BattlePreparedViewService(BattleTransportAdmission admission,BattleArtifactStore artifacts,BattleProfileArchive profiles,
    BattleExecutorRegistry executors,SessionRegistry sessions,OutpostClock clock)
{
    internal async Task<(BattlePreparedEntry Entry, byte[] Inventory)?> ReadEntryAsync(Session session, GuildWorldScope request, CancellationToken ct)
    {
        if (request is null || request.CalculateSize() > 1024 || session.GuildVersion != 1) throw Invalid();
        request = request.Clone();
        await using var held = await admission.HoldAsync(ct);
        if (request.WorldInstanceId != held.Cut.WorldId || request.RestoreBoundary != held.Cut.Boundary) throw Invalid();
        BattleCommanderIdentity.RequireStillCurrent(sessions, session);
        var current = await (from battle in held.Db.Set<GuildBattle>().AsNoTracking()
            join campaign in held.Db.Set<GuildCampaign>().AsNoTracking() on battle.CampaignId equals campaign.Id
            where battle.WorldId == request.WorldInstanceId && battle.Boundary == request.RestoreBoundary
                && battle.Active && campaign.Active
                && (battle.Phase == "Prepared" && battle.StartDigest == null && campaign.Phase == "PreparingBattle"
                    || battle.Phase == "StartCommitted" && battle.StartDigest != null && battle.FinalDigest == null && campaign.Phase == "StartCommitted")
                && (campaign.AttackerCommanderId == session.PlayerId || campaign.DefenderCommanderId == session.PlayerId)
            select new { Battle = battle, Campaign = campaign }).SingleOrDefaultAsync(ct);
        if (current is null) return null;
        var preparedBattle = current.Battle;
        bool running = preparedBattle.Phase == "StartCommitted";
        if (!running && clock.Sample().Utc >= current.Campaign.ScheduledAtUtc.AddMinutes(5)) return null;
        var document = ReadManifest(preparedBattle, running);
        if (document is null) return null;
        var manifest = document.Value.Manifest;
        if (!running && manifest.Attacker.PlayerId != session.PlayerId && (manifest.Defender.DefenderAi || manifest.Defender.PlayerId != session.PlayerId))
            return null;
        var scope = new BattleScopeMessage { WorldInstanceId = preparedBattle.WorldId, RestoreBoundary = preparedBattle.Boundary,
            BattleId = preparedBattle.Id, Attempt = preparedBattle.Attempt, Fence = preparedBattle.Fence };
        var (_, approved) = await RequireViewerHeld(held, session, scope, preparedBattle, current.Campaign, manifest, running, ct, verifyAssets: true);
        var profile = approved!.Profile;
        var entry = new BattlePreparedEntry
        {
            Scope = scope,
            ProfileDigest = manifest.ProfileDigest,
            Profile = ByteString.CopyFrom(profiles.ReadHeld(approved.Approval.Request.Profile)),
            ViewerCompatibility = ByteString.CopyFrom(profiles.ReadHeld(profiles.Reference(profile.ViewerCompatibility))),
        };
        var inventory = approved.Inventory.ToByteArray();
        BattleCommanderIdentity.RequireStillCurrent(sessions, session);
        return (entry, inventory);
    }

    internal async Task<BattlePreparedViewReply> ReadAsync(Session session,BattlePreparedViewRequest request,CancellationToken ct)
    {
        request=Freeze(request);await using var held=await admission.HoldAsync(ct);
        var (manifest, raw, digest, side, running) = await RequireHeld(held, session, request, ct);
        return new() { Manifest = ByteString.CopyFrom(raw), ManifestDigest = ByteString.CopyFrom(digest),
            InitialSnapshot = manifest.InitialSnapshot.Clone(), Side = side, StartCommitted = running };
    }
    internal async Task<BattlePreparedInitialReply> ReadInitialAsync(Session session,BattlePreparedInitialRequest request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>2048 || request.ManifestDigest.Length!=32 || request.Offset<0)throw Invalid();
        request=request.Clone();var view=Freeze(request.View);await using var held=await admission.HoldAsync(ct);
        var (manifest, _, digest, _, _) = await RequireHeld(held, session, view, ct, verifyAssets: false);
        if (!request.ManifestDigest.Span.SequenceEqual(digest) || request.Offset >= manifest.InitialSnapshot.SizeBytes) throw Invalid();
        byte[] bytes = artifacts.ReadHeld(InitialDescriptor(manifest), request.Offset);
        return new(){ManifestDigest=request.ManifestDigest,Offset=request.Offset,Data=ByteString.CopyFrom(bytes),Complete=request.Offset+bytes.Length==manifest.InitialSnapshot.SizeBytes};
    }
    private async Task<(BattlePreparedManifestV1 Manifest, byte[] Raw, byte[] Digest, BattleFighterSide Side, bool Running)> RequireHeld(BattleTransportAdmission.Lease held,Session session,BattlePreparedViewRequest request,CancellationToken ct,bool verifyAssets=true)
    {
        var scope=request.Scope;var home=request.Home;var colony=home.Scope;
        if(session.GuildVersion!=1 || colony.PlayerId!=session.PlayerId || colony.SaveWriteEpoch!=session.SaveWriteEpoch
            || scope.WorldInstanceId!=held.Cut.WorldId || scope.RestoreBoundary!=held.Cut.Boundary || colony.WorldInstanceId!=scope.WorldInstanceId || colony.RestoreBoundary!=scope.RestoreBoundary)throw Invalid();
        BattleCommanderIdentity.RequireStillCurrent(sessions,session);
        var battle=await held.Db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b=>b.Id==scope.BattleId,ct)??throw Invalid();
        var campaign=await held.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c=>c.Id==battle.CampaignId,ct);
        bool running = battle.Phase == "StartCommitted";
        var (manifest, raw, digest) = ReadManifest(battle, running) ?? throw Invalid();
        if (!manifest.ProfileDigest.Equals(request.ProfileDigest)) throw Invalid();
        var (side, _) = await RequireViewerHeld(held, session, scope, battle, campaign, manifest, running, ct, verifyAssets);
        var player=await held.Db.Players.AsNoTracking().SingleAsync(p=>p.Id==session.PlayerId,ct);
        var version=await held.Db.SaveVersions.AsNoTracking().SingleOrDefaultAsync(v=>v.PlayerId==session.PlayerId && v.IsCurrent,ct);
        var link=await held.Db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(s=>s.PlayerId==session.PlayerId && s.WorldId==colony.WorldInstanceId
            && s.RestoreBoundary==colony.RestoreBoundary && s.SaveGeneration==colony.SaveGeneration && s.SaveWriteEpoch==colony.SaveWriteEpoch && s.SnapshotId==home.SnapshotId,ct);
        if(player.SaveGeneration!=colony.SaveGeneration || player.SaveWriteEpoch!=colony.SaveWriteEpoch || version is null || version.Number!=request.HomeVersion
            || version.SnapshotId!=home.SnapshotId || version.SaveGeneration!=colony.SaveGeneration || version.SaveWriteEpoch!=colony.SaveWriteEpoch
            || version.RawSha256 is null || !version.RawSha256.AsSpan().SequenceEqual(home.RawSha256.Span) || link is null
            || !link.RawSha256.AsSpan().SequenceEqual(home.RawSha256.Span)
            || OutpostSaveMetadata.Parser.ParseFrom(link.Metadata).GuildMetadata?.Scope.Equals(colony)!=true)throw Invalid();
        BattleCommanderIdentity.RequireStillCurrent(sessions, session); return (manifest, raw, digest, side, running);
    }

    private (BattlePreparedManifestV1 Manifest, byte[] Raw, byte[] Digest)? ReadManifest(GuildBattle battle, bool running)
    {
        if (running)
        {
            if (battle.StartManifest is not { } raw || battle.StartDigest is not { Length: 32 } digest
                || !BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest, raw).AsSpan().SequenceEqual(digest)) throw Invalid();
            return (BattleManifestContracts.ReadPrepared(raw), raw, digest);
        }
        var proposal = new BattlePreparedJournal(artifacts).LatestHeld(battle.Id, battle.Fence);
        return proposal is null ? null : (BattleManifestContracts.ReadPrepared(proposal.Manifest), proposal.Manifest, proposal.ManifestDigest);
    }

    private async Task<(BattleFighterSide Side, ApprovedGuildBattleProfile? Approved)> RequireViewerHeld(
        BattleTransportAdmission.Lease held, Session session, BattleScopeMessage scope, GuildBattle battle, GuildCampaign campaign,
        BattlePreparedManifestV1 manifest, bool running, CancellationToken ct, bool verifyAssets)
    {
        if (!running) return await RequirePreparedHeld(held, session, scope, battle, campaign, manifest, ct, verifyAssets);
        if (!battle.Active || battle.FinalDigest is not null || !campaign.Active || campaign.Phase != "StartCommitted"
            || battle.WorldId != scope.WorldInstanceId || battle.Boundary != scope.RestoreBoundary || battle.Attempt != scope.Attempt || battle.Fence != scope.Fence
            || manifest.BattleId != battle.Id || manifest.CampaignId != campaign.Id || manifest.WorldInstanceId != battle.WorldId
            || manifest.RestoreBoundary != battle.Boundary || manifest.Attempt != battle.Attempt || manifest.Fence != battle.Fence
            || manifest.ExecutorId != battle.ExecutorId) throw Invalid();

        BattleFighterSide side = campaign.AttackerCommanderId == session.PlayerId ? BattleFighterSide.Attacker
            : campaign.DefenderCommanderId == session.PlayerId ? BattleFighterSide.Defender : throw Invalid();
        long alliance = side == BattleFighterSide.Attacker ? campaign.AttackerAllianceId : campaign.DefenderAllianceId;
        var member = await held.Db.Set<AllianceMember>().SingleOrDefaultAsync(m => m.PlayerId == session.PlayerId && m.AllianceId == alliance, ct);
        var original = side == BattleFighterSide.Attacker ? manifest.Attacker : manifest.Defender;
        if (member is null || !original.DefenderAi && (original.PlayerId != session.PlayerId || member.JoinedAt.Ticks != original.MembershipJoinedUtcTicks)
            || !await held.Db.Players.AnyAsync(p => p.Id == session.PlayerId && p.SaveWriteEpoch == session.SaveWriteEpoch, ct)) throw Invalid();

        var executor = executors.RequireExecutionFor(manifest);
        var approval = await GuildAuthorityEvidence.ProfileAdmissionAsync(held.Db, battle.WorldId, executor.ExecutorId, executor.ProfileDigest.ToByteArray(), ct);
        if (approval.TransitionId != manifest.ApprovalTransitionId) throw Invalid();
        ApprovedGuildBattleProfile? approved = null;
        if (verifyAssets)
        {
            approved = await GuildAuthorityEvidence.RequireApprovedProfileAsync(held.Db, profiles, approval.TransitionId,
                executor.ExecutorId, executor.ProfileDigest.ToByteArray(), ct);
            _ = artifacts.ValidateCompleteHeld(InitialDescriptor(manifest));
        }
        BattleCommanderIdentity.RequireStillCurrent(sessions, session);
        return (side, approved);
    }

    private static BattleArtifactDescriptor InitialDescriptor(BattlePreparedManifestV1 manifest) =>
        new(1, manifest.BattleId, manifest.WorldInstanceId, manifest.RestoreBoundary, manifest.ExecutorId, manifest.Fence,
            "initial", manifest.InitialSnapshot.SizeBytes, Convert.ToHexStringLower(manifest.InitialSnapshot.Sha256.Span));

    private async Task<(BattleFighterSide Side, ApprovedGuildBattleProfile? Approved)> RequirePreparedHeld(BattleTransportAdmission.Lease held,
        Session session, BattleScopeMessage scope, GuildBattle battle, GuildCampaign campaign, BattlePreparedManifestV1 manifest,
        CancellationToken ct, bool verifyAssets)
    {
        if(!battle.Active || battle.Phase!="Prepared" || battle.StartDigest is not null || !campaign.Active || campaign.Phase!="PreparingBattle"
            || battle.WorldId!=scope.WorldInstanceId || battle.Boundary!=scope.RestoreBoundary || battle.Attempt!=scope.Attempt || battle.Fence!=scope.Fence
            || clock.Sample().Utc>=campaign.ScheduledAtUtc.AddMinutes(5))throw Invalid();
        var preparation=await GuildAuthorityEvidence.PreparationAsync(held.Db,battle.Id,ct);var executor=executors.RequireAssignedExecution(preparation.RuntimeAssignment);
        ApprovedGuildBattleProfile? approved = null;
        if(verifyAssets) approved = await BattlePreparedAdmission.ValidateHeldAsync(held.Db,manifest,battle,campaign,executor,profiles,artifacts,sessions,ct,requireDefenderSession:false);
        else
        {
            var approval=await GuildAuthorityEvidence.ProfileAdmissionAsync(held.Db,battle.WorldId,executor.ExecutorId,executor.ProfileDigest.ToByteArray(),ct);
            var assignment=preparation.RuntimeAssignment;
            if(manifest.WorldInstanceId!=battle.WorldId || manifest.RestoreBoundary!=battle.Boundary || manifest.CampaignId!=campaign.Id
                || manifest.Attempt!=battle.Attempt || manifest.Fence!=battle.Fence || manifest.OutpostId!=campaign.OutpostId || manifest.OwnerRevision!=campaign.OwnerRevision
                || manifest.AttackerAllianceId!=campaign.AttackerAllianceId || manifest.DefenderAllianceId!=campaign.DefenderAllianceId
                || manifest.ExecutorId!=executor.ExecutorId || manifest.RuntimeInstanceId!=assignment.RuntimeInstanceId || manifest.ExecutorLeaseId!=assignment.OriginalLeaseId
                || manifest.ExecutorLeaseFence!=assignment.OriginalLeaseFence || !manifest.ProfileDigest.Equals(executor.ProfileDigest)
                || manifest.ApprovalTransitionId!=approval.TransitionId || manifest.ApprovalTransitionId!=assignment.ApprovalTransitionId
                || manifest.PreparationTransitionId!=preparation.TransitionId || !manifest.StartNonce.Span.SequenceEqual(battle.StartNonce)
                || manifest.Attacker.PlayerId!=campaign.AttackerCommanderId || manifest.Attacker.AllianceId!=campaign.AttackerAllianceId
                || manifest.Attacker.ControlEpoch!=manifest.ProposalSequence || !manifest.Defender.DefenderAi && (manifest.Defender.PlayerId!=campaign.DefenderCommanderId
                    || manifest.Defender.AllianceId!=campaign.DefenderAllianceId || manifest.Defender.ControlEpoch!=manifest.ProposalSequence))throw Invalid();
            await BattleCommanderIdentity.RequireAsync(held.Db,sessions,manifest.Attacker,campaign.AttackerAllianceId,campaign.AttackerCommanderId,manifest.ProposalSequence,false,ct);
        }
        BattleFighterSide side;BattleCommanderBinding commander;
        if(manifest.Attacker.PlayerId==session.PlayerId){side=BattleFighterSide.Attacker;commander=manifest.Attacker;}
        else if(!manifest.Defender.DefenderAi && manifest.Defender.PlayerId==session.PlayerId){side=BattleFighterSide.Defender;commander=manifest.Defender;}
        else throw Invalid();
        var actual=await BattleCommanderIdentity.RequireAsync(held.Db,sessions,commander,commander.AllianceId,session.PlayerId,manifest.ProposalSequence,false,ct);
        if(!ReferenceEquals(actual,session))throw Invalid();
        return (side, approved);
    }
    private static BattlePreparedViewRequest Freeze(BattlePreparedViewRequest request)
    {
        if(request is null || request.CalculateSize()>1536 || request.Scope is null || request.Home?.Scope is null || request.HomeVersion<=0
            || request.ProfileDigest.Length!=32 || request.Home.RawSha256.Length!=32 || !Guid.TryParseExact(request.Home.SnapshotId,"N",out var snapshot) || snapshot==Guid.Empty
            || !Guid.TryParseExact(request.Scope.BattleId,"D",out var battle) || battle==Guid.Empty || request.Scope.Attempt<=0 || request.Scope.Fence<=0)throw Invalid();
        GuildResourceContracts.ValidateScope(request.Home.Scope);return request.Clone();
    }
    private static InvalidDataException Invalid()=>new("battle-prepared-view-refused");
}
