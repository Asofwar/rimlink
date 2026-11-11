using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

// A replacement proof is refused when the process observation is older than ten seconds. Under the whole
// suite on Windows the commit can wait longer than that for the machine, so this class runs outside the parallel pool.
[CollectionDefinition(nameof(OwnedProcessTiming), DisableParallelization = true)]
public sealed class OwnedProcessTiming;

[Collection(nameof(OwnedProcessTiming))]
public sealed class GuildRestoreTests
{
    [Fact]
    public async Task Restore_keeps_an_accepted_guild_delivery_in_the_canonical_save_without_reissuing_it()
    {
        using var f = await GuildFixture.Create();
        var h = f.H;
        var metadata = await GuildReceiptAcceptanceTests.Applied(f);
        var guard = new OutpostSnapshotGuard(h.Db, h.Files);
        var selected = await GuildSnapshotTests.Accept(f, guard, metadata, 3);
        var receipt = metadata.GuildMetadata.Receipts.Single();
        var wealth = new ServerWealth(h.Db, Options.Create(new EconomyOptions { ServerWealthPercent = 100 }));
        Assert.Equal(0, await wealth.Of(1));
        var request = await RestoreDeliveryArchive(f, "guild-accepted.tar.zst");
        var canonical = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        Assert.Equal(selected.Version.RawSha256, canonical.RawSha256);
        await RebaseDeliveryMetadata(h, metadata, canonical, request.TargetBoundary);

        var missingProof = metadata.Clone();
        missingProof.RestoreBridge = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => GuildSnapshotTests.Accept(f, guard, missingProof, 4));
        var changed = metadata.Clone();
        changed.GuildMetadata.Receipts[0].PayloadDigest = ByteString.CopyFrom(new byte[32]);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => GuildSnapshotTests.Accept(f, guard, changed, 4));
        changed.GuildMetadata.Receipts.Clear();
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => GuildSnapshotTests.Accept(f, guard, changed, 4));
        await GuildSnapshotTests.Accept(f, guard, metadata, 4);
        Assert.Equal(receipt.ToByteArray(), (await h.Db.Set<GuildDeliveryAttempt>().AsNoTracking().SingleAsync()).Receipt);
        Assert.Equal(selected.Version.SnapshotId, (await h.Db.Set<GuildParcelLink>().AsNoTracking().SingleAsync()).AcceptedSnapshotId);
        Assert.Single(await h.Db.Parcels.ToArrayAsync());
        Assert.Equal(0, await wealth.Of(1));
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_reauthorizes_an_issued_guild_delivery_from_its_exact_before_save(bool started)
    {
        using var f = await GuildFixture.Create();
        var h = f.H;
        var old = await GuildDeliveryStartTests.Prepared(f);
        var options = Options.Create(new EconomyOptions { ServerWealthPercent = 100 });
        var wealth = new ServerWealth(h.Db, options);
        Assert.Equal(13, await wealth.Of(1));
        var guard = new OutpostSnapshotGuard(h.Db, h.Files);
        GuildDeliveryReceipt? abandoned = null;
        if (started)
        {
            var prior = await f.Service.BeginDeliveryAsync(1, old, default);
            var claim = await h.Db.Set<GuildResourceClaim>().AsNoTracking().SingleAsync();
            abandoned = new() { SchemaVersion = 1, AttemptId = prior.AttemptId, ClaimId = claim.Id, ParcelId = old.ParcelId,
                ClientRequestId = old.ClientRequestId, PayloadDigest = old.PayloadDigest, AuthorizationTransitionId = prior.TransitionId,
                AuthorizationTransitionDigest = prior.TransitionDigest, AuthorizationIntentDigest = prior.IntentDigest,
                BeforeSnapshot = old.BeforeSnapshot.Clone() };
        }
        var request = await RestoreDeliveryArchive(f, "guild-issued.tar.zst", async () =>
        {
            if (abandoned is null) return;
            var discarded = GuildSnapshotTests.Metadata(f);
            discarded.GuildMetadata.Receipts.Add(abandoned);
            await GuildSnapshotTests.Accept(f, guard, discarded, 3);
            Assert.Equal(0, await wealth.Of(1));
        }, started ? "AfterMainDatabaseCommit" : null);
        Assert.Equal(13, await wealth.Of(1));
        var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        var mailbox = new MailboxService(h.Db, events, new WalletService(h.Db, events), options, h.Files);
        var parcel = await mailbox.ToProto(await mailbox.Get(1, old.ParcelId));
        Assert.Equal(old.Scope, parcel.GuildResourcePayload.Recipient);
        Assert.Equal(old.PayloadDigest, ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(parcel.GuildResourcePayload.ToByteArray())));
        var scope = GuildResourceContracts.DeliveryScope(parcel);
        Assert.Equal(request.TargetBoundary, scope.RestoreBoundary);
        Assert.Equal(1, scope.PlayerId);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.BeginDeliveryAsync(1, old, default));
        var canonical = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var metadata = SaveStore.RecoveryOf(canonical).Outposts.Metadata;
        await RebaseDeliveryMetadata(h, metadata, canonical, request.TargetBoundary);
        var current = old.Clone();
        current.ClientRequestId = Guid.NewGuid().ToString("D");
        current.Scope = scope.Clone();
        current.BeforeSnapshot.Scope = scope.Clone();
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.BeginDeliveryAsync(1, current, default));

        var before = await GuildSnapshotTests.Accept(f, guard, metadata, 3);
        if (abandoned is not null)
        {
            var retired = await h.Db.Set<GuildDeliveryAttempt>().AsNoTracking().SingleAsync();
            Assert.False(retired.Active);
            Assert.Null(retired.Receipt);
            var late = metadata.Clone();
            late.GuildMetadata.Receipts.Add(abandoned);
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => GuildSnapshotTests.Accept(f, guard, late, 4));
        }
        current.BeforeSnapshot.SnapshotId = before.Version.SnapshotId;
        current.BeforeSnapshot.RawSha256 = ByteString.CopyFrom(before.Version.RawSha256!);
        var authorization = await f.Service.BeginDeliveryAsync(1, current, default);
        Assert.Equal(13, await wealth.Of(1));
        metadata.GuildMetadata.Receipts.Add(new GuildDeliveryReceipt { SchemaVersion = 1, AttemptId = authorization.AttemptId,
            ClaimId = parcel.GuildResourcePayload.ClaimId, ParcelId = current.ParcelId, ClientRequestId = current.ClientRequestId,
            PayloadDigest = current.PayloadDigest, AuthorizationTransitionId = authorization.TransitionId,
            AuthorizationTransitionDigest = authorization.TransitionDigest, AuthorizationIntentDigest = authorization.IntentDigest,
            BeforeSnapshot = current.BeforeSnapshot.Clone() });
        await GuildSnapshotTests.Accept(f, guard, metadata, 4);
        Assert.True((await f.Service.BeginDeliveryAsync(1, current, default)).AckOnly);
        Assert.Equal(0, await wealth.Of(1));
        Assert.Single(await h.Db.Parcels.ToArrayAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    private static async Task<RestoreRequest> RestoreDeliveryArchive(GuildFixture f, string archiveName, Func<Task>? afterBackup = null,
        string? interruption = null)
    {
        var h = f.H;
        string archive = Path.Combine(h.Data.Backups, archiveName);
        await new OutpostBackupCapture(h.Db, h.Data, h.Authority, h.Anchor, null!, f.Clock, new LiveStateLock()).CreateAsync(archive, default);
        if (afterBackup is not null) await afterBackup();
        var procedure = new OutpostRestoreProcedure(f.Time);
        string anchor = Path.GetDirectoryName(h.Anchor.DatabasePath)!;
        var inspected = await procedure.InspectAsync(archive, h.Data.Root, anchor, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(h.Data.Root));
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        h.MainObservation.Dispose();
        await h.Db.Database.CloseConnectionAsync();
        RestoreResult restored;
        if (interruption is null) restored = await procedure.ExecuteAsync(request, default);
        else
        {
            procedure.Checkpoint = point => { if (point == interruption) throw new Interruption(); };
            await Assert.ThrowsAsync<Interruption>(() => procedure.ExecuteAsync(request, default));
            restored = await new OutpostRestoreProcedure(f.Time).ResumeAsync(h.Data.Root, anchor, request.RestoreId, default);
        }
        Assert.True(restored.Outcome == RestoreOutcome.Completed, restored.Reason);
        h.Db.ChangeTracker.Clear();
        return request;
    }

    private static async Task RebaseDeliveryMetadata(AuthorityHarness h, OutpostSaveMetadata metadata, SaveVersion canonical, string boundary)
    {
        await using (var lease = await h.Authority.AcquireAsync([1], default))
            metadata.RestoreBridge = await new OutpostRestoreEvidence(h.Db, h.Files).GetDownloadBridgeHeldAsync(lease, 1, canonical, default);
        Assert.NotNull(metadata.RestoreBridge);
        metadata.Scope.RestoreBoundary = boundary;
        metadata.GuildMetadata.Scope.RestoreBoundary = boundary;
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("AfterMainPlanWritten", false)]
    [InlineData("AfterMainPlanBound", false)]
    [InlineData("AfterMainPrepared", false)]
    [InlineData("AfterMainDatabaseCommit", false)]
    [InlineData("AfterMainCommitted", false)]
    [InlineData("AfterCompletionCommitted", false)]
    [InlineData("AfterMainPlanBound", true)]
    public async Task Restore_preserves_stock_and_legacy_history_but_revokes_an_unassigned_campaign(string? checkpoint, bool tamper)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        var activation = await OutpostAdminActivationTests.CommandForCurrent(h);
        var admin = OutpostAdminActivationTests.Service(h);
        await admin.ActivateDomainFormatAsync(Actor.Api, activation, default);
        h.Db.Add(new Player { Id = 98000001, Name = "guild-restore-enemy", Account = new Account { Id = 98000001, Login = "guild-restore-enemy" } });
        h.Db.AddRange(new AllianceGroup { Id = 50, OwnerId = 1, Name = "defenders" }, new AllianceGroup { Id = 51, OwnerId = 98000001, Name = "attackers" });
        h.Db.AddRange(new AllianceMember { PlayerId = 1, AllianceId = 50, JoinedAt = DateTime.UnixEpoch }, new AllianceMember { PlayerId = 98000001, AllianceId = 51, JoinedAt = DateTime.UnixEpoch });
        await h.Db.SaveChangesAsync(); h.Db.ChangeTracker.Clear();
        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        var preview = await admin.PreviewActivateGuildDomainFormatAsync(Actor.Api, Guid.NewGuid().ToString("D"), "guild restore fixture", default);
        Assert.NotNull(preview.Command);
        await admin.ActivateDomainFormatAsync(Actor.Api, preview.Command with { Confirmed = true }, default);
        f.Host.Dispose();
        h.Db.ChangeTracker.Clear(); var head = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        string objectId = Guid.NewGuid().ToString("D"), campaignId = Guid.NewGuid().ToString("D");
        var clock = new OutpostClock(f.H.F.Time); var sample = clock.Sample();
        await using (var lease = await h.Authority.AcquireAsync([], default))
            await h.Authority.CommitHeldAsync(lease, new(Guid.NewGuid().ToString("D"), head.WorldId, 0, 0, head.RestoreBoundary,
                // Storage-only setup through the existing system primitive; not campaign or combat acceptance.
                "Production", "fixture", new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.CopyFromUtf8("guild-restore-fixture")), async (db, ct) =>
            {
                db.Add(new GuildOutpostRow { Id = objectId, WorldId = head.WorldId, Tile = 4321, Resource = GuildResource.Steel, AllianceId = 50,
                    Revision = 1, Stock = 37, Remainder = 11, Phase = "Active", CheckpointUtc = sample.Utc, ClockProcessId = sample.ProcessId,
                    ClockTimestamp = sample.Timestamp, ClockFrequency = sample.Frequency });
                db.Add(new GuildCampaign { Id = campaignId, OutpostId = objectId, WorldId = head.WorldId, Boundary = head.RestoreBoundary,
                    OwnerRevision = 1, AttackerAllianceId = 51, DefenderAllianceId = 50, AttackerCommanderId = 98000001, Active = true,
                    RulesDigest = new byte[32], DeclaredAtUtc = sample.Utc, ScheduledAtUtc = sample.Utc.AddDays(1) });
                db.Add(new GuildAcquisitionReservation { Id = Guid.NewGuid().ToString("D"), CampaignId = campaignId, AllianceId = 51, Active = true });
                db.Add(new GuildBattleSlot { Id = Guid.NewGuid().ToString("D"), CampaignId = campaignId, ExecutorId = "fixture", StartsAtUtc = sample.Utc.AddDays(1), EndsAtUtc = sample.Utc.AddDays(1).AddMinutes(30), Active = true });
                await db.SaveChangesAsync(ct);
            }, default);
        h.Db.ChangeTracker.Clear();
        string archive = Path.Combine(h.Data.Backups, "guild-v3.tar.zst");
        await new OutpostBackupCapture(h.Db, h.Data, h.Authority, h.Anchor, f.H.F.Service, clock, new LiveStateLock()).CreateAsync(archive, default);
        var manifest = OutpostBackupManifest.Decode(OutpostBackupTests.Read(archive)[OutpostBackupManifest.FileName]);
        Assert.Equal(3, manifest.Version); Assert.Equal(OutpostDomainFormat.V3, manifest.Domain!.Format);
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var request = await OutpostRestoreTests.Request(f, procedure, archive);
        h.MainObservation.Dispose(); await h.Db.Database.CloseConnectionAsync();
        f.H.F.Time.Advance(TimeSpan.FromDays(4));
        RestoreResult result;
        if (checkpoint is not null)
        {
            procedure.Checkpoint = point => { if (point == checkpoint) throw new Interruption(); };
            await Assert.ThrowsAsync<Interruption>(() => procedure.ExecuteAsync(request, default));
            if (tamper)
            {
                string path = Path.Combine(Path.GetDirectoryName(h.Anchor.DatabasePath)!, "restores", request.RestoreId, "main.json");
                var altered = OutpostRestoreEvidence.Decode<RestoreMainPlan>(File.ReadAllBytes(path));
                altered.Guild!.Outposts[0].After.Stock++;
                File.WriteAllBytes(path, OutpostRestoreEvidence.Encode(altered));
            }
            result = await new OutpostRestoreProcedure(f.H.F.Time).ResumeAsync(h.Data.Root, Path.GetDirectoryName(h.Anchor.DatabasePath)!, request.RestoreId, default);
        }
        else result = await procedure.ExecuteAsync(request, default);
        if (tamper)
        {
            Assert.Equal(RestoreOutcome.Maintenance, result.Outcome);
            h.Db.ChangeTracker.Clear();
            Assert.Equal(37, (await h.Db.Set<GuildOutpostRow>().SingleAsync()).Stock);
            Assert.Empty(await h.Db.OutpostTransitions.Where(t => t.Id == request.RestoreId).ToArrayAsync());
            return;
        }
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
        h.Db.ChangeTracker.Clear();
        var restored = await h.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal(50, restored.AllianceId); Assert.Equal(1, restored.Revision); Assert.Equal(37, restored.Stock); Assert.Equal(11, restored.Remainder);
        Assert.Equal("Active", restored.Phase); Assert.True(restored.CheckpointUtc > sample.Utc);
        Assert.False((await h.Db.Set<GuildCampaign>().SingleAsync()).Active);
        Assert.Empty(await h.Db.Set<GuildBattle>().ToArrayAsync());
        Assert.False((await h.Db.Set<GuildAcquisitionReservation>().SingleAsync()).Active);
        Assert.False((await h.Db.Set<GuildBattleSlot>().SingleAsync()).Active);
        Assert.Equal(request.TargetBoundary, (await h.Db.OutpostAuthority.SingleAsync()).RestoreBoundary);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, false, true)]
    public async Task Restore_preserves_the_exact_terminal_assignment_and_occupied_arena(bool applied, bool replace, bool manual, bool startedAbort)
    {
        if (replace && !OperatingSystem.IsWindows()) return;
        using var f = await GuildFixture.Create(true);
        await using var originalProcess = replace ? new OwnedExecutorTestProcess() : null;
        await using var replacementProcess = replace ? new OwnedExecutorTestProcess() : null;
        if (originalProcess is not null)
        {
            OwnedExecutorTestProcess.Select(f.H.Data.Root);
            f.LocalWitness = new(f.H.Data);
            f.LocalProcess = await originalProcess.ObserveAsync(f.LocalWitness);
            Assert.False(f.LocalWitness.Exited(f.LocalProcess));
        }
        BattleExecutorLease executor;
        BattleScopeMessage scope;
        if (applied || startedAbort)
        {
            var (lease, envelope, start) = await GuildBattleResultTests.Prepare(f);
            executor = lease;
            if (startedAbort)
            {
                string head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
                await f.Service.AbortStartedBattleAsync(Actor.Api,
                    new(f.Scope, Guid.NewGuid().ToString("D"), head, start.BattleId, "Operator stop"), default);
                scope = new() { WorldInstanceId = start.WorldInstanceId, RestoreBoundary = start.RestoreBoundary,
                    BattleId = start.BattleId, Attempt = start.Attempt, Fence = start.Fence };
            }
            else
            {
                var result = await f.Service.ApplyBattleFinalAsync(executor, envelope, default);
                scope = GuildBattleCleanupTests.Scope(result.Receipt);
            }
        }
        else
        {
            var (request, start) = await GuildBattleStartTests.Prepare(f, false);
            var original = BattleResultStaging.Lease(request.Executor);
            var binding = f.Executors.BindingOf(original)!;
            string boot = f.Executors.Wire(original).ServerBootId;
            if (!manual) f.Time.Advance(TimeSpan.FromMinutes(5));
            executor = f.Executors.RegisterExecutionAuthenticated(binding, boot, f.LocalProcess);
            string requestId = Guid.NewGuid().ToString("D");
            if (manual)
            {
                string head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
                await f.Service.CancelCampaignAsync(Actor.Api, f.Scope, requestId, start.CampaignId, head, "Operator maintenance", default);
            }
            else await f.Service.ExpireCampaignAsync(f.Scope, requestId, start.CampaignId, default);
            scope = request.Scope;
        }

        var db = f.H.Db;
        var battle = await db.Set<GuildBattle>().AsNoTracking().SingleAsync();
        Assert.True(battle.Active);
        Assert.Equal(applied ? "AppliedAwaitingCleanup" : "CancelledAwaitingCleanup", battle.Phase);
        var plan = await GuildRestore.CreateAsync(db, battle.WorldId, Guid.NewGuid().ToString("D"), f.Time.Now.UtcDateTime, default);
        var map = Assert.Single(plan.Battles);
        Assert.True(OutpostRestoreEvidence.Same(battle, map.Before));
        Assert.True(OutpostRestoreEvidence.Same(battle, map.After));
        var campaign = await db.Set<GuildCampaign>().AsNoTracking().SingleAsync();
        var outpost = await db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        var obligations = await db.Set<GuildResourceObligation>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(applied ? 60 : 50, outpost.AllianceId);
        Assert.Equal(applied ? 2 : 1, outpost.Revision);
        if (applied) Assert.Equal(50, Assert.Single(obligations).AllianceId);
        else Assert.Empty(obligations);

        var h = f.H;
        string archive = Path.Combine(h.Data.Backups, "guild-terminal.tar.zst");
        await new OutpostBackupCapture(db, h.Data, h.Authority, h.Anchor, null!, f.Clock, new LiveStateLock()).CreateAsync(archive, default);
        var procedure = new OutpostRestoreProcedure(f.Time);
        string anchorDir = Path.GetDirectoryName(h.Anchor.DatabasePath)!;
        var inspected = await procedure.InspectAsync(archive, h.Data.Root, anchorDir, default);
        var requestRestore = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(h.Data.Root));
        requestRestore = requestRestore with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(requestRestore) };

        await h.MainObservation.DrainAsync(default);
        await db.Database.CloseConnectionAsync();
        var refused = await procedure.ExecuteAsync(requestRestore, default);
        Assert.Equal(RestoreOutcome.Refused, refused.Outcome);
        Assert.True(refused.Reason.Contains("guild-restore-destination-arena-occupied", StringComparison.Ordinal), refused.Reason);
        Assert.False(Directory.Exists(Path.Combine(anchorDir, "restores", requestRestore.RestoreId)));
        Assert.Equal(inspected.Destination.External, (await procedure.InspectAsync(archive, h.Data.Root, anchorDir, default)).Destination.External);

        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        var cleanup = GuildBattleCleanupTests.Request(executor, await f.Service.CleanupChallengeAsync(executor, scope, default));
        await f.Service.CleanupBattleAsync(executor, cleanup.ClientRequestId, cleanup.Proof, default);
        var cleanInspection = await procedure.InspectAsync(archive, h.Data.Root, anchorDir, default);
        requestRestore = requestRestore with { RestoreId = Guid.NewGuid().ToString("D"), Inspection = cleanInspection };
        requestRestore = requestRestore with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(requestRestore) };
        h.MainObservation.Dispose();
        await db.Database.CloseConnectionAsync();

        string crash = applied ? "AfterMainDatabaseCommit" : "AfterMainPrepared";
        procedure.Checkpoint = point => { if (point == crash) throw new Interruption(); };
        await Assert.ThrowsAsync<Interruption>(() => procedure.ExecuteAsync(requestRestore, default));
        var restored = await new OutpostRestoreProcedure(f.Time).ResumeAsync(h.Data.Root, anchorDir, requestRestore.RestoreId, default);
        Assert.True(restored.Outcome == RestoreOutcome.Completed, restored.Reason);
        db.ChangeTracker.Clear();
        Assert.Equal(requestRestore.TargetBoundary, (await db.OutpostAuthority.AsNoTracking().SingleAsync()).RestoreBoundary);
        Assert.True(OutpostRestoreEvidence.Same(battle, await db.Set<GuildBattle>().AsNoTracking().SingleAsync()));
        Assert.True(OutpostRestoreEvidence.Same(campaign, await db.Set<GuildCampaign>().AsNoTracking().SingleAsync()));
        Assert.True(OutpostRestoreEvidence.Same(obligations, await db.Set<GuildResourceObligation>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
        var restoredOutpost = await db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal((outpost.AllianceId, outpost.Revision, outpost.Stock), (restoredOutpost.AllianceId, restoredOutpost.Revision, restoredOutpost.Stock));
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.CleanupBattleAsync(executor, cleanup.ClientRequestId, cleanup.Proof, default));
        var restarted = new BattleExecutorRegistry(f.Time, "arbiter-1", f.ExpectedProfile);
        using var observation = new OutpostMainObservation(h.Data);
        await OutpostMainObservationTests.Prepare(h, observation);
        var admin = new OutpostAdminService(db, h.Authority, new LiveStateLock(), null!, observation, null!);
        var occupied = await admin.InspectGuildCaptureAsync(Actor.Api, restarted, 1, default);
        Assert.Equal(requestRestore.TargetBoundary, occupied.Scope?.Boundary);
        var observedBattle = Assert.Single(occupied.Battles);
        Assert.Equal((battle.Id, battle.Phase, battle.Boundary), (observedBattle.Id, observedBattle.Phase, observedBattle.Boundary));
        var hello = new BattleExecutorHelloService(restarted, new(f.Time, Guid.NewGuid().ToString("D")),
            new(h.Data, h.Files, h.Authority, h.Feature, f.Battles), f.Profiles, f.Artifacts, f.Clock, f.LocalWitness);
        var entry = f.ExecutorBinding();
        var staleEntry = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => hello.BeginAsync(entry, "credential", default));
        Assert.Equal("battle-executor-hello-admission-refused", staleEntry.Message);
        entry.RestoreBoundary = requestRestore.TargetBoundary;
        var freshEntry = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => hello.BeginAsync(entry, "credential", default));
        Assert.Equal("battle-executor-hello-admission-refused", freshEntry.Message);
        Assert.Null(restarted.Available());
        Assert.True((await db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Active);

        var target = await GuildAuthorityEvidence.CleanupTargetAsync(db, battle, default);
        if (startedAbort) Assert.Equal(BattleCleanupTerminalKind.StartedAbort, target.Terminal.Kind);
        var assignment = target.Assignment.Message();
        var recovery = entry.Clone();
        recovery.Entry = null;
        recovery.Lane = BattleExecutorLane.RecoveryDelivery;
        recovery.BattleId = battle.Id;
        recovery.Attempt = battle.Attempt;
        recovery.Fence = battle.Fence + 1;
        recovery.AssignmentTransitionId = assignment.TransitionId;
        recovery.StartDigest = target.StartDigest;
        recovery.RetainedContext = new() { Version = 1, Phase = BattleExecutorRetainedPhase.Entry,
            StartNonce = assignment.StartNonce, AssignmentRejected = true };
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => hello.BeginAsync(recovery, "credential", default));
        recovery.RuntimeInstanceId = assignment.RuntimeInstanceId;
        var staleFence = recovery.Clone();
        staleFence.Fence = battle.Fence;
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => hello.BeginAsync(staleFence, "credential", default));
        var wrongNonce = recovery.Clone();
        wrongNonce.RetainedContext.StartNonce = ByteString.CopyFrom(new byte[32]);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => hello.BeginAsync(wrongNonce, "credential", default));
        var restoredService = f.ForExecutor(restarted);
        var restoredScope = scope.Clone();
        restoredScope.RestoreBoundary = recovery.RestoreBoundary;
        restoredScope.Fence = recovery.Fence;
        if (replacementProcess is not null)
        {
            var copiedIdentity = await hello.BeginAsync(recovery, "credential", default);
            await replacementProcess.AnswerAsync(copiedIdentity);
            var wrongProcess = BattleResultStaging.Lease(await hello.CompleteAsync(new() { Binding = recovery, ChallengeId = copiedIdentity.ChallengeId,
                Nonce = copiedIdentity.Nonce, ServerBootId = copiedIdentity.ServerBootId }, "credential", default));
            await Assert.ThrowsAsync<InvalidDataException>(() => restoredService.CleanupChallengeAsync(wrongProcess, restoredScope, default));
            recovery.Entry = entry.Entry.Clone();
            recovery.ReplaceRestoredExecutor = true;
            recovery.RetainedContext = null;
            recovery.RuntimeInstanceId = entry.RuntimeInstanceId;
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => hello.BeginAsync(recovery, "credential", default));
            OwnedExecutorTestProcess.Select(h.Data.Root, battle.Id, requestRestore.TargetBoundary);
        }
        var challenge = await hello.BeginAsync(recovery, "credential", default);
        Assert.Equal(replace, challenge.LocalProcessRequired);
        if (replacementProcess is not null) await replacementProcess.AnswerAsync(challenge);
        var restoredLease = BattleResultStaging.Lease(await hello.CompleteAsync(new() { Binding = recovery, ChallengeId = challenge.ChallengeId,
            Nonce = challenge.Nonce, ServerBootId = challenge.ServerBootId }, "credential", default));
        var fresh = await restoredService.CleanupChallengeAsync(restoredLease, restoredScope, default);
        var proof = GuildBattleCleanupTests.Request(restoredLease, fresh);
        if (originalProcess is not null)
        {
            proof.Proof.ProofKind = BattleCleanupProofKind.ProvedReplacement;
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() =>
                restoredService.CleanupBattleAsync(restoredLease, proof.ClientRequestId, proof.Proof, default));
            Assert.Null(restarted.Available());
            Assert.True((await db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Active);
            await originalProcess.CloseAsync();
            Assert.True(f.LocalWitness!.Exited(f.LocalProcess!));
        }
        var committed = await restoredService.CleanupBattleAsync(restoredLease, proof.ClientRequestId, proof.Proof, default);
        Assert.Equal(proof.Proof.ProofKind, committed.ProofKind);
        Assert.Equal(committed, await restoredService.CleanupBattleAsync(restoredLease, proof.ClientRequestId, proof.Proof, default));
        var replayChallenge = await hello.BeginAsync(recovery, "credential", default);
        if (replacementProcess is not null) await replacementProcess.AnswerAsync(replayChallenge);
        var replayLease = BattleResultStaging.Lease(await hello.CompleteAsync(new() { Binding = recovery,
            ChallengeId = replayChallenge.ChallengeId, Nonce = replayChallenge.Nonce, ServerBootId = replayChallenge.ServerBootId }, "credential", default));
        Assert.Equal(committed, (await restoredService.CleanupChallengeAsync(replayLease, restoredScope, default)).CommittedReceipt);
        Assert.Equal(restoredScope, committed.Scope);
        Assert.False((await db.Set<GuildBattle>().AsNoTracking().SingleAsync()).Active);
        Assert.Empty((await admin.InspectGuildCaptureAsync(Actor.Api, restarted, 1, default)).Battles);
        Assert.True(OutpostRestoreEvidence.Same(obligations, await db.Set<GuildResourceObligation>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
        var entryChallenge = await hello.BeginAsync(entry, "credential", default);
        if (replacementProcess is not null) await replacementProcess.AnswerAsync(entryChallenge);
        var admitted = await hello.CompleteAsync(new() { Binding = entry, ChallengeId = entryChallenge.ChallengeId,
            Nonce = entryChallenge.Nonce, ServerBootId = entryChallenge.ServerBootId }, "credential", default);
        Assert.Equal(BattleResultStaging.Lease(admitted), restarted.Available());
    }

    [Fact]
    public async Task Restore_plan_refuses_an_assignment_without_a_terminal_receipt()
    {
        using var f = await GuildFixture.Create(true);
        await GuildBattleStartTests.Prepare(f, false);
        var error = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() =>
            GuildRestore.CreateAsync(f.H.Db, f.Scope.WorldId, Guid.NewGuid().ToString("D"), f.Time.Now.UtcDateTime, default));
        Assert.Equal("guild-restore-active-battle-reconciliation-required", error.Message);
    }

    [Theory]
    [InlineData("issued")]
    [InlineData("authorized")]
    [InlineData("accepted")]
    public async Task Colony_rollback_refuses_to_rebase_a_save_with_an_issued_guild_resource(string state)
    {
        using var f = await GuildFixture.Create();
        var h = f.H;
        if (state == "accepted")
        {
            var metadata = await GuildReceiptAcceptanceTests.Applied(f);
            await GuildSnapshotTests.Accept(f, new OutpostSnapshotGuard(h.Db, h.Files), metadata, 3);
        }
        else
        {
            var request = await GuildDeliveryStartTests.Prepared(f);
            if (state == "authorized")
            {
                await f.Service.BeginDeliveryAsync(1, request, default);
                await GuildSnapshotTests.Accept(f, new(h.Db, h.Files), GuildSnapshotTests.Metadata(f), 3);
                var refusal = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => GuildRestore.RequireSupportedSourceAsync(h.Db, default));
                Assert.Equal("guild-delivery-restore-unqualified", refusal.Message);
            }
        }
        var target = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.Number == 2);
        var lifecycle = new OutpostLifecycle(h.Db, h.Authority, h.Files, null!, null!, f.Clock);
        await using var lease = await h.Authority.AcquireAsync([1], default);
        var decision = await lifecycle.CheckRollbackHeldAsync(lease, 1, target, default);
        Assert.False(decision.Allowed);
        Assert.Equal("guild-delivery-rollback-unqualified", decision.Reason);
    }

    private sealed class Interruption : Exception;
}
