using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildProfileApprovalRequest(GuildMutationScope Scope,string RequestId,GuildBattleDocument Profile,
    GuildBattleDocument Qualification,string ExecutorId,string Reason);
internal sealed record GuildProfileApprovalReceipt(int Version,GuildProfileApprovalRequest Request,string ProfileId,long Revision,
    string ProfileDigest,string InventoryDigest,long OperatorId,string OperatorName,DateTime ApprovedAtUtc,string TransitionId)
{
    internal string ExecutorId => Request.ExecutorId;
}
internal sealed record GuildProfileRevokeRequest(GuildMutationScope Scope,string ApprovalTransitionId,string Reason);
internal sealed record GuildProfileRevokeReceipt(int Version,GuildProfileRevokeRequest Request,long OperatorId,string OperatorName,DateTime RevokedAtUtc,string TransitionId);
internal sealed record ApprovedGuildBattleProfile(GuildProfileApprovalReceipt Approval,BattleProfileV1 Profile,BattleRuntimeInventoryV1 Inventory);

internal sealed partial class GuildOutpostService
{
    internal async Task<GuildBattleDocument> StageProfileDocumentAsync(Actor actor,BattleDocumentKind kind,Stream body,CancellationToken ct)
    {
        actor.Require(Role.Admin);authority.RequireRuntimeGuild(GuildRuntimeUse.Setup);
        if(kind is not(BattleDocumentKind.Profile or BattleDocumentKind.Inventory or BattleDocumentKind.Config or BattleDocumentKind.Defs
            or BattleDocumentKind.Template or BattleDocumentKind.ViewerCompatibility or BattleDocumentKind.Qualification))throw Refused();
        using var bytes=new MemoryStream();byte[] buffer=new byte[BattleArtifactStore.ChunkBytes];int read;
        while((read=await body.ReadAsync(buffer,ct))!=0)
        {
            if(bytes.Length+read>BattleArtifactStore.MaximumBytes)throw new InvalidDataException("guild-profile-document-too-large");
            bytes.Write(buffer,0,read);
        }
        await using var held=await authority.AcquireAsync([],ct);
        if((await authority.CheckHeldAsync(held,ct)).State!=OutpostStartupState.Ready)throw Refused();
        authority.RequireRuntimeGuild(GuildRuntimeUse.Setup);
        return profiles.PutHeld(kind,bytes.GetBuffer().AsSpan(0,checked((int)bytes.Length)));
    }
    internal Task<GuildProfileApprovalReceipt> ApproveBattleProfileAsync(Actor actor,GuildProfileApprovalRequest request,CancellationToken ct)
    {
        actor.Require(Role.Admin);
        if (request?.Scope is null || request.Profile is null || request.Qualification is null || !ProfileText(request.ExecutorId,128)
            || !ProfileText(request.Reason,500) || request.Profile.Kind != BattleDocumentKind.Profile || request.Qualification.Kind != BattleDocumentKind.Qualification) throw Refused();
        return MutateAsync(request.Scope,actor.PlayerId,actor.Name,request.RequestId,"GuildBattleProfileApproved",request,async (lease,id,token) =>
        {
            var (profile,inventory) = GuildAuthorityEvidence.ReadProfileClosure(profiles,request);
            var prior = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildBattleProfileApproved").Select(o => o.Receipt).ToArrayAsync(token);
            if (prior.Select(GuildAuthorityEvidence.ReadProfileApproval).Any(r => r.ProfileId == profile.ProfileId && r.Revision == profile.Revision)) throw Refused();
            return new GuildProfileApprovalReceipt(1,request,profile.ProfileId,profile.Revision,
                Convert.ToHexStringLower(BattleManifestContracts.Digest(BattleDocumentKind.Profile,profiles.ReadHeld(request.Profile))),
                Convert.ToHexStringLower(BattleManifestContracts.Digest(BattleDocumentKind.Inventory,profiles.ReadHeld(profiles.Reference(profile.Inventory)))),
                actor.PlayerId,actor.Name,clock.Sample().Utc,id);
        },ct);
    }
    internal Task<GuildProfileRevokeReceipt> RevokeBattleProfileAsync(Actor actor,GuildMutationScope scope,string requestId,string approvalId,string reason,CancellationToken ct)
    {
        actor.Require(Role.Admin);
        if (!Guid.TryParseExact(approvalId,"D",out _) || !ProfileText(reason,500)) throw Refused();
        var request = new GuildProfileRevokeRequest(scope,approvalId,reason);
        return MutateAsync(scope,actor.PlayerId,actor.Name,requestId,"GuildBattleProfileRevoked",request,async (lease,id,token) =>
        {
            var approval = await GuildAuthorityEvidence.ProfileApprovalAsync(db,approvalId,token);
            if (approval.Request.Scope.WorldId != scope.WorldId || await GuildAuthorityEvidence.ProfileRevokedAsync(db,approvalId,token)) throw Refused();
            var affected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var battle in await db.Set<GuildBattle>().AsNoTracking().Where(b => b.StartManifest != null).ToArrayAsync(token))
                if (BattleManifestContracts.ReadPrepared(battle.StartManifest!).ApprovalTransitionId == approvalId) affected.Add(battle.Id);
            foreach (var ready in await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildAttackerReady").Select(o => o.Receipt).ToArrayAsync(token))
            {
                var receipt = GuildAuthorityEvidence.ReadReadyReceipt(ready);
                var proposed = new BattlePreparedJournal(artifacts).ReadHeld(receipt.Manifest.BattleId,receipt.Manifest.Fence,(int)receipt.GrantEpoch);
                if (BattleManifestContracts.ReadPrepared(proposed.Manifest).ApprovalTransitionId == approvalId) affected.Add(receipt.Manifest.BattleId);
            }
            foreach (var grant in await db.Set<GuildBattleControlGrant>().Where(g => g.Active).ToArrayAsync(token))
                if (affected.Contains(grant.BattleId)) grant.Active = false;
            return new GuildProfileRevokeReceipt(1,request,actor.PlayerId,actor.Name,clock.Sample().Utc,id);
        },ct,battleLock:true);
    }
    private static bool ProfileText(string value,int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}

internal static partial class GuildAuthorityEvidence
{
    internal static GuildProfileApprovalReceipt ReadProfileApproval(byte[] bytes) => JsonSerializer.Deserialize<GuildProfileApprovalReceipt>(bytes,Json) ?? throw InvalidProfile();
    internal static async Task<GuildProfileApprovalReceipt> ProfileApprovalAsync(RimlinkDbContext db,string transitionId,CancellationToken ct)
    {
        var operation = await db.Set<GuildCaptureOperation>().AsNoTracking().SingleOrDefaultAsync(o => o.Kind == "GuildBattleProfileApproved" && o.TransitionId == transitionId,ct);
        return operation is null ? throw InvalidProfile() : ReadProfileApproval(operation.Receipt);
    }
    internal static async Task<bool> ProfileRevokedAsync(RimlinkDbContext db,string approvalId,CancellationToken ct)
    {
        var records = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildBattleProfileRevoked").Select(o => o.Receipt).ToArrayAsync(ct);
        return records.Select(b => JsonSerializer.Deserialize<GuildProfileRevokeReceipt>(b,Json) ?? throw InvalidProfile()).Any(r => r.Request.ApprovalTransitionId == approvalId);
    }
    internal static async Task<GuildProfileApprovalReceipt> ProfileAdmissionAsync(RimlinkDbContext db,string world,string executorId,byte[] digest,CancellationToken ct)
    {
        var records = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildBattleProfileApproved").Select(o => o.Receipt).ToArrayAsync(ct);
        string expected = Convert.ToHexStringLower(digest);
        var matches = records.Select(ReadProfileApproval).Where(r => r.Request.Scope.WorldId == world && r.ExecutorId == executorId && r.ProfileDigest == expected).ToArray();
        if (matches.Length != 1 || await ProfileRevokedAsync(db,matches[0].TransitionId,ct)) throw InvalidProfile();
        return matches[0];
    }
    internal static async Task<ApprovedGuildBattleProfile> RequireApprovedProfileAsync(RimlinkDbContext db,BattleProfileArchive archive,
        string approvalId,string executorId,byte[] digest,CancellationToken ct)
    {
        var approval = await ProfileApprovalAsync(db,approvalId,ct);
        if (approval.ExecutorId != executorId || !digest.AsSpan().SequenceEqual(Convert.FromHexString(approval.ProfileDigest))
            || await ProfileRevokedAsync(db,approvalId,ct)) throw InvalidProfile();
        var (profile,inventory) = ReadProfileClosure(archive,approval.Request);
        return new(approval,profile,inventory);
    }
    internal static (BattleProfileV1 Profile,BattleRuntimeInventoryV1 Inventory) ReadProfileClosure(BattleProfileArchive archive,GuildProfileApprovalRequest request)
    {
        if (request.Profile.Kind != BattleDocumentKind.Profile || request.Qualification.Kind != BattleDocumentKind.Qualification) throw InvalidProfile();
        var profile = BattleManifestContracts.ReadProfile(archive.ReadHeld(request.Profile));
        var inventory = BattleManifestContracts.ReadInventory(archive.ReadHeld(archive.Reference(profile.Inventory)));
        if (!profile.ExecutorBuildSha256.Equals(inventory.ExecutorBuildSha256)) throw InvalidProfile();
        _ = archive.ReadHeld(archive.Reference(inventory.Config)); _ = archive.ReadHeld(archive.Reference(inventory.LoadedDefs));
        _ = archive.ReadHeld(archive.Reference(profile.Template)); _ = archive.ReadHeld(archive.Reference(profile.ViewerCompatibility));
        _ = archive.ReadHeld(request.Qualification);
        return (profile,inventory);
    }
    private static void ValidateProfileApprovals(BattleProfileArchive archive,GuildCaptureOperation[] operations,
        IReadOnlyDictionary<string,OutpostTransition> transitions)
    {
        var approvals = new Dictionary<string,GuildProfileApprovalReceipt>(StringComparer.Ordinal);
        var revisions = new HashSet<(string,long)>();
        try
        {
            foreach (var operation in operations.Where(o => o.Kind == "GuildBattleProfileApproved"))
            {
                var receipt = ReadProfileApproval(operation.Receipt);
                if (receipt.Version != 1 || receipt.Request?.Scope is null || receipt.OperatorId != operation.ActorId || receipt.TransitionId != operation.TransitionId
                    || receipt.Request.RequestId != operation.RequestId || receipt.ApprovedAtUtc.Kind != DateTimeKind.Utc
                    || !transitions.TryGetValue(receipt.TransitionId,out var transition) || receipt.Request.Scope.WorldId != transition.WorldId
                    || receipt.Request.Scope.Boundary != transition.RestoreBoundary || receipt.OperatorName != transition.Actor
                    || !IntentDigest(receipt.Request).AsSpan().SequenceEqual(operation.IntentDigest) || !revisions.Add((receipt.ProfileId,receipt.Revision))) throw InvalidProfile();
                var (profile,inventory) = ReadProfileClosure(archive,receipt.Request);
                if (receipt.ProfileId != profile.ProfileId || receipt.Revision != profile.Revision
                    || receipt.ProfileDigest != Convert.ToHexStringLower(BattleManifestContracts.Digest(BattleDocumentKind.Profile,archive.ReadHeld(receipt.Request.Profile)))
                    || receipt.InventoryDigest != Convert.ToHexStringLower(BattleManifestContracts.Digest(BattleDocumentKind.Inventory,archive.ReadHeld(archive.Reference(profile.Inventory))))) throw InvalidProfile();
                approvals.Add(receipt.TransitionId,receipt);
            }
            var revoked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in operations.Where(o => o.Kind == "GuildBattleProfileRevoked"))
            {
                var receipt = JsonSerializer.Deserialize<GuildProfileRevokeReceipt>(operation.Receipt,Json) ?? throw InvalidProfile();
                if (receipt.Version != 1 || receipt.Request?.Scope is null || receipt.OperatorId != operation.ActorId || receipt.TransitionId != operation.TransitionId
                    || receipt.RevokedAtUtc.Kind != DateTimeKind.Utc || !approvals.TryGetValue(receipt.Request.ApprovalTransitionId,out var approval)
                    || !revoked.Add(receipt.Request.ApprovalTransitionId)
                    || receipt.Request.Scope.WorldId != approval.Request.Scope.WorldId || !IntentDigest(receipt.Request).AsSpan().SequenceEqual(operation.IntentDigest)
                    || !transitions.TryGetValue(receipt.TransitionId,out var transition) || receipt.Request.Scope.WorldId != transition.WorldId
                    || receipt.Request.Scope.Boundary != transition.RestoreBoundary || receipt.OperatorName != transition.Actor) throw InvalidProfile();
                RequireEarlier(transitions,approval.TransitionId,receipt.TransitionId);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or ArgumentException or NullReferenceException)
        { throw new OutpostEvidenceUnavailableException("guild-profile-evidence-invalid: " + error.GetType().Name); }
    }
    private static OutpostEvidenceUnavailableException InvalidProfile() => new("guild-profile-not-approved-or-revoked");
}
