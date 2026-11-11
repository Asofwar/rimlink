using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Role = Rimlink.Server.Storage.Entities.Role;
using OutpostAuthority = Rimlink.Server.Application.Outposts.OutpostAuthority;
using Rimlink.Server.Application.Economy;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildMutationScope(string WorldId, string Boundary);
internal sealed record GuildOutpostResult(GuildMutationScope Scope, string OutpostId, long Revision, int Stock, string TransitionId);
internal sealed record GuildConsentTerms(int Version, GuildMutationScope Scope, string OutpostId, long Revision,
    long AllianceId, long LeaderId, GuildResource Resource, int DefenceHourUtc, int HourlyYield, int Capacity,
    int ProtectionHours, int MaximumOutposts, bool AutomaticCapture, bool OldStockRemainsWithPreviousAlliance);
internal sealed record GuildConsentPreview(GuildConsentTerms Terms, byte[] Digest);

// Internal until the Product readiness, home isolation and delivery gates are qualified.
internal sealed partial class GuildOutpostService(RimlinkDbContext db, OutpostAuthority authority, LiveStateLock live,
    OutpostClock clock, OutpostManifestService manifests, OutpostTileClaims tiles, GuildCapturePolicy policy, MailboxService mailbox, BattleExecutorRegistry executors, BattleProfileArchive profiles, BattleArtifactStore artifacts, BattleStateLock battles, Sessions.SessionRegistry sessions, BattleResultStaging resultStaging, BattleCleanupChallenges cleanupChallenges,
    GuildOwnedProcessWitness? processWitness = null)
{
    internal Task<GuildOutpostResult> PlaceAsync(Actor actor, GuildMutationScope scope, string requestId,
        int tile, GuildResource resource, string reason, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        ValidatePlacement(tile, resource, reason);
        return MutateAsync(scope, actor.PlayerId, actor.Name, requestId, "GuildPlacement",
            new { actor, scope, tile, resource, reason },
            (lease, id, token) => StagePlacementAsync(lease, scope, tile, resource, id, token), ct);
    }

    internal async Task<GuildConsentPreview> PreviewConsentAsync(long leader, long alliance, GuildMutationScope scope,
        string outpostId, int defenceHourUtc, CancellationToken ct)
    {
        if (leader <= 0 || alliance <= 0 || defenceHourUtc is < 0 or > 23 || !Guid.TryParseExact(outpostId, "D", out _)) throw Refused();
        authority.RequireRuntimeGuild(GuildRuntimeUse.Setup);
        await using var lease = await authority.AcquireAsync([leader], ct);
        using var heldLive = await live.Hold();
        await RequireScopeAsync(lease, scope, ct);
        await policy.RequireLeaderHeldAsync(lease, leader, alliance, clock.Sample().Utc, ct);
        var row = await db.Set<GuildOutpostRow>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == outpostId && o.WorldId == scope.WorldId, ct) ?? throw Refused();
        if (row.AllianceId is not null || row.Phase != "Neutral") throw Refused();
        var terms = Terms(scope, row, leader, alliance, defenceHourUtc);
        return new(terms, GuildAuthorityEvidence.IntentDigest(terms));
    }

    internal async Task<GuildOutpostResult> AcceptConsentAsync(long leader, long alliance, GuildMutationScope scope,
        string requestId, string outpostId, int defenceHourUtc, byte[] expectedTermsDigest, CancellationToken ct)
    {
        if (leader <= 0 || alliance <= 0 || defenceHourUtc is < 0 or > 23 || expectedTermsDigest is not { Length: 32 }
            || !Guid.TryParseExact(outpostId, "D", out _)) throw Refused();
        byte[] digest = expectedTermsDigest.ToArray();
        return await MutateAsync(scope, leader, "player:" + leader, requestId, "GuildConsent",
            new { scope, leader, alliance, outpostId, defenceHourUtc, digest }, async (lease, id, token) =>
            {
                var sample = clock.Sample();
                await policy.RequireLeaderHeldAsync(lease, leader, alliance, sample.Utc, token);
                var row = await db.Set<GuildOutpostRow>().SingleOrDefaultAsync(o => o.Id == outpostId && o.WorldId == scope.WorldId, token) ?? throw Refused();
                if (row.AllianceId is not null || row.Phase != "Neutral" || row.Stock != 0 || row.Remainder != 0
                    || !CryptographicOperations.FixedTimeEquals(GuildAuthorityEvidence.IntentDigest(Terms(scope, row, leader, alliance, defenceHourUtc)), digest)) throw Refused();
                int owned = await db.Set<GuildOutpostRow>().CountAsync(o => o.WorldId == scope.WorldId && o.AllianceId == alliance, token);
                int reserved = await db.Set<GuildAcquisitionReservation>().CountAsync(r => r.AllianceId == alliance && r.Active, token);
                if (owned + reserved >= 3) throw Refused();
                db.Add(new GuildCaptureConsent { Id = Guid.NewGuid().ToString("D"), OutpostId = row.Id, AllianceId = alliance,
                    LeaderId = leader, OwnerRevision = row.Revision + 1, RulesVersion = 1, DefenceHourUtc = defenceHourUtc,
                    Digest = digest, AcceptedAtUtc = sample.Utc });
                row.AllianceId = alliance; row.Revision++; row.Phase = "Active"; row.DefenceHourUtc = defenceHourUtc;
                row.ProtectedUntilUtc = GuildBattleSchedule.ProtectedUntil(sample.Utc, sample.Utc); Checkpoint(row, sample);
                return Result(scope, row, id);
            }, ct);
    }

    internal Task<GuildOutpostResult> SettleAsync(GuildMutationScope scope, string requestId, string outpostId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(outpostId, "D", out _)) throw Refused();
        return MutateAsync(scope, 0, "system:guild-production", requestId, "GuildProduction", new { scope, outpostId }, async (_, id, token) =>
        {
            var row = await db.Set<GuildOutpostRow>().SingleOrDefaultAsync(o => o.Id == outpostId && o.WorldId == scope.WorldId, token) ?? throw Refused();
            var sample = clock.Sample();
            var checkpoint = new ProductionCheckpoint(row.CheckpointUtc, row.ClockProcessId, row.ClockTimestamp, row.ClockFrequency);
            bool paused = row.AllianceId is null || row.Phase != "Active";
            var stock = GuildProduction.Settle(row.Resource, new(row.Stock, row.Remainder), OutpostClock.Elapsed(checkpoint, sample), paused);
            var next = OutpostClock.Advance(checkpoint, sample);
            row.Stock = stock.Count; row.Remainder = stock.Remainder; Checkpoint(row, new(next.Utc, next.ProcessId, next.Timestamp, next.Frequency));
            return Result(scope, row, id);
        }, ct);
    }

    private async Task<TResult> MutateAsync<T, TResult>(GuildMutationScope scope, long player, string actor, string requestId,
        string kind, T intent, Func<OutpostLease, string, CancellationToken, Task<TResult>> stage, CancellationToken ct,
        IReadOnlyList<long>? additionalOwners = null, Func<CancellationToken, Task>? validateScope = null, long saveGeneration = 0, bool battleLock = false, Action<TResult>? afterCommit = null)
    {
        if (!Guid.TryParseExact(requestId, "D", out _) || scope is null || player < 0) throw Refused();
        authority.RequireRuntimeGuild(GuildRuntimeAdmission.MutationUse(kind));
        byte[] intentDigest = GuildAuthorityEvidence.IntentDigest(intent);
        await using var lease = await authority.AcquireAsync((player == 0 ? [] : new[] { player }).Concat(additionalOwners ?? []).ToArray(), ct);
        using var heldLive = await live.Hold();
        using var heldBattle = battleLock ? await battles.HoldAsync(ct) : null;
        var root = await RequireScopeAsync(lease, scope, ct);
        authority.RequireRuntimeGuild(GuildRuntimeAdmission.MutationUse(kind));
        if (validateScope is not null) await validateScope(ct);
        var previous = await db.Set<GuildCaptureOperation>().AsNoTracking().SingleOrDefaultAsync(o => o.ActorId == player && o.RequestId == requestId, ct);
        if (previous is not null)
        {
            if (previous.Kind != kind || !previous.IntentDigest.AsSpan().SequenceEqual(intentDigest)) throw Refused();
            var replay = JsonSerializer.Deserialize<TResult>(previous.Receipt) ?? throw Refused();
            afterCommit?.Invoke(replay);
            return replay;
        }
        string id = Guid.NewGuid().ToString("D");
        var transition = new TransitionIntent(id, scope.WorldId, player, saveGeneration, scope.Boundary, kind, actor,
            new(root.HeadId, ByteString.CopyFrom(root.HeadDigest)), ByteString.Empty);
        await using var transaction = await authority.BeginHeldAsync(lease, transition, ct);
        var result = await stage(lease, id, ct);
        var operation = new GuildCaptureOperation { Id = Guid.NewGuid().ToString("D"), ActorId = player, RequestId = requestId,
            IntentDigest = intentDigest, Kind = kind, Receipt = JsonSerializer.SerializeToUtf8Bytes(result), TransitionId = id };
        db.Add(operation);
        await transaction.CompleteAsync(GuildAuthorityEvidence.Binding(operation), ct);
        afterCommit?.Invoke(result);
        return result;
    }

    private async Task<Storage.Entities.OutpostAuthority> RequireScopeAsync(OutpostLease lease, GuildMutationScope scope, CancellationToken ct)
    {
        if ((await authority.CheckHeldAsync(lease, ct)).State != OutpostStartupState.Ready
            || authority.VerifiedDomainFormat != OutpostDomainFormat.V3) throw Refused();
        var root = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        if (root.WorldId != scope.WorldId || root.RestoreBoundary != scope.Boundary) throw Refused();
        return root;
    }

    private static GuildConsentTerms Terms(GuildMutationScope scope, GuildOutpostRow row, long leader, long alliance, int hour) =>
        new(1, scope, row.Id, row.Revision, alliance, leader, row.Resource, hour,
            row.Resource == GuildResource.Steel ? 10 : 20, 1000, 72, 3, true, true);
    private static GuildOutpostResult Result(GuildMutationScope scope, GuildOutpostRow row, string transitionId) =>
        new(scope, row.Id, row.Revision, row.Stock, transitionId);
    private static void Checkpoint(GuildOutpostRow row, ClockSample sample)
    { row.CheckpointUtc = sample.Utc; row.ClockProcessId = sample.ProcessId; row.ClockTimestamp = sample.Timestamp; row.ClockFrequency = sample.Frequency; }
    private static OutpostEvidenceUnavailableException Refused() => new("guild-outpost-operation-refused");
}
