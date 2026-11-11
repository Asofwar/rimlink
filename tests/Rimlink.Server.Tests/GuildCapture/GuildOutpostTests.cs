using OutpostAuthority=Rimlink.Server.Application.Outposts.OutpostAuthority;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;
using Wallet = Rimlink.Server.Storage.Entities.Wallet;
using WorldObject = Rimlink.Server.Storage.Entities.WorldObject;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildOutpostTests
{
    [Fact]
    public async Task Explicit_consent_starts_only_future_production_and_replay_keeps_one_owner_revision()
    {
        using var f = await GuildFixture.Create();
        var placed = await f.Place(0);
        f.Time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, (await f.Service.SettleAsync(f.Scope, Request(), placed.OutpostId, default)).Stock);
        var preview = await f.Service.PreviewConsentAsync(1, 50, f.Scope, placed.OutpostId, 23, default);
        Assert.True(preview.Terms.AutomaticCapture); Assert.True(preview.Terms.OldStockRemainsWithPreviousAlliance);
        Assert.Equal(10, preview.Terms.HourlyYield);
        string request = Request();
        var accepted = await f.Service.AcceptConsentAsync(1, 50, f.Scope, request, placed.OutpostId, 23, preview.Digest, default);
        Assert.Equal(accepted, await f.Service.AcceptConsentAsync(1, 50, f.Scope, request, placed.OutpostId, 23, preview.Digest, default));
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(10, (await f.Service.SettleAsync(f.Scope, Request(), placed.OutpostId, default)).Stock);
        var row = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal(1, row.Revision); Assert.Equal(50, row.AllianceId); Assert.Equal(23, row.DefenceHourUtc);
        Assert.Equal(f.Time.Now.UtcDateTime.AddHours(71), row.ProtectedUntilUtc);
        Assert.Single(await f.H.Db.Set<GuildCaptureConsent>().ToArrayAsync());
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
        Assert.False(f.H.Feature.IsAvailable);
    }

    [Fact]
    public async Task Placement_reserves_the_tile_against_all_existing_entry_points()
    {
        using var f = await GuildFixture.Create();
        await f.Place(0);
        await using var lease = await f.H.Authority.AcquireAsync([], default);
        Assert.True(await f.Tiles.IsOutpostOccupiedHeldAsync(0));
        await Assert.ThrowsAsync<RpcException>(() => f.Tiles.RequireAvailableHeldAsync(lease, "planet", 0, null, default));
    }

    [Theory]
    [InlineData("digest")][InlineData("window")][InlineData("leader")][InlineData("peaceful")]
    [InlineData("newbie")][InlineData("frozen")][InlineData("debt")][InlineData("generation")]
    public async Task Stale_or_ineligible_consent_has_no_owner_stock_or_receipt_effect(string change)
    {
        using var f = await GuildFixture.Create(); var placed = await f.Place(0);
        var preview = await f.Service.PreviewConsentAsync(1, 50, f.Scope, placed.OutpostId, 4, default);
        long leader = 1; int hour = 4;
        if (change == "digest") preview.Digest[0] ^= 1;
        if (change == "window") hour++;
        if (change == "leader") leader = 2;
        var player = await f.H.Db.Players.SingleAsync(p => p.Id == 1);
        if (change == "peaceful") player.Peaceful = true;
        if (change == "newbie") player.FirstSaveAt = f.Time.Now.UtcDateTime;
        if (change == "frozen") player.EconomyFrozenAt = f.Time.Now.UtcDateTime;
        if (change == "generation") player.SaveGeneration++;
        if (change == "debt") f.H.Db.Wallets.Add(new Wallet { PlayerId = 1, Balance = -1 });
        await f.H.Db.SaveChangesAsync(); f.H.Db.ChangeTracker.Clear();
        int count = await f.H.Db.OutpostTransitions.CountAsync();
        if (change is "frozen" or "debt")
            await Assert.ThrowsAsync<RpcException>(() => f.Service.AcceptConsentAsync(leader, 50, f.Scope, Request(), placed.OutpostId, hour, preview.Digest, default));
        else
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.AcceptConsentAsync(leader, 50, f.Scope, Request(), placed.OutpostId, hour, preview.Digest, default));
        var row = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Null(row.AllianceId); Assert.Equal(0, row.Stock); Assert.Equal(0, row.Revision);
        Assert.Empty(await f.H.Db.Set<GuildCaptureConsent>().ToArrayAsync());
        Assert.Equal(count, await f.H.Db.OutpostTransitions.CountAsync());
    }

    [Fact]
    public async Task Three_owned_slots_refuse_a_fourth_consent_without_consuming_the_object()
    {
        using var f = await GuildFixture.Create();
        for (int tile = 0; tile < 3; tile++)
        {
            var placed = await f.Place(tile);
            var consent = await f.Service.PreviewConsentAsync(1, 50, f.Scope, placed.OutpostId, 4, default);
            await f.Service.AcceptConsentAsync(1, 50, f.Scope, Request(), placed.OutpostId, 4, consent.Digest, default);
        }
        var fourth = await f.Place(3);
        var preview = await f.Service.PreviewConsentAsync(1, 50, f.Scope, fourth.OutpostId, 4, default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.AcceptConsentAsync(1, 50, f.Scope, Request(), fourth.OutpostId, 4, preview.Digest, default));
        Assert.Null((await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync(o => o.Id == fourth.OutpostId)).AllianceId);
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Consent_crash_and_retry_commit_exactly_once(string checkpoint, bool committed)
    {
        using var f = await GuildFixture.Create(); var placed = await f.Place(0);
        var preview = await f.Service.PreviewConsentAsync(1, 50, f.Scope, placed.OutpostId, 4, default);
        string request = Request();
        f.H.Authority.CommitCheckpoint = point => { if (point == checkpoint) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => f.Service.AcceptConsentAsync(1, 50, f.Scope, request, placed.OutpostId, 4, preview.Digest, default));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed ? 1 : 0, await f.H.Db.Set<GuildCaptureConsent>().CountAsync());
        await f.Service.AcceptConsentAsync(1, 50, f.Scope, request, placed.OutpostId, 4, preview.Digest, default);
        Assert.Single(await f.H.Db.Set<GuildCaptureConsent>().ToArrayAsync());
        Assert.Equal(1, (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Revision);
    }

    private sealed class Interrupted : Exception;
    private static string Request() => Guid.NewGuid().ToString("D");
}

internal sealed class GuildFixture : IDisposable
{
    private readonly ManifestFixture manifest;
    internal AuthorityHarness H => manifest.H;
    internal ManualTimeProvider Time { get; } = new() { Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero) };
    internal GuildOutpostService Service { get; private set; } = null!;
    internal BattleExecutorRegistry Executors { get; private set; } = null!;
    internal BattleProfileArchive Profiles { get; private set; } = null!;
    internal BattleArtifactStore Artifacts { get; private set; } = null!;
    internal BattleStateLock Battles { get; } = new();
    internal BattleCleanupChallenges CleanupChallenges { get; private set; } = null!;
    internal SessionRegistry Sessions { get; } = new();
    internal OutpostClock Clock { get; }
    internal byte[] ExpectedProfile { get; private set; } = new byte[32];
    private Func<BattleExecutorRegistry,OutpostAuthority,OutpostClock,GuildOutpostService> serviceFactory = null!;
    private Rimlink.Protocol.V1.BattleProfileV1? knownProfile;
    private readonly string executorBoot=Guid.NewGuid().ToString("D");
    internal void UseExecutorProfile(byte[] digest,Rimlink.Protocol.V1.BattleProfileV1? profile=null)
    {knownProfile=profile??knownProfile;ExpectedProfile=digest.ToArray();Executors=new(Time,"arbiter-1",digest);Service=serviceFactory(Executors,H.Authority,Clock);}
    internal string ExecutorBoot=>executorBoot;
    internal BattleExecutorHelloBindingV1 ExecutorBinding()=>new(){Version=1,ExecutorId="arbiter-1",RuntimeInstanceId=Guid.NewGuid().ToString("D"),
        ProfileDigest=Google.Protobuf.ByteString.CopyFrom(ExpectedProfile),InventorySha256=knownProfile?.Inventory.Sha256??Google.Protobuf.ByteString.CopyFrom(new byte[32]),
        ExecutorBuildSha256=knownProfile?.ExecutorBuildSha256??Google.Protobuf.ByteString.CopyFrom(new byte[32]),Lane=BattleExecutorLane.Execution,Entry=new(){Version=1,AtEntry=true},
        WorldInstanceId=Scope.WorldId,RestoreBoundary=Scope.Boundary};
    internal GuildOwnedProcess? LocalProcess { get; set; }
    internal GuildOwnedProcessWitness? LocalWitness { get; set; }
    internal BattleExecutorLease RegisterExecutor()=>Executors.RegisterExecutionAuthenticated(ExecutorBinding(),executorBoot,LocalProcess);
    internal BattleExecutorLease RegisterRecovery(BattlePreparedManifestV1 start)=>Executors.RegisterRecoveryAuthenticated(new(){Version=1,ExecutorId=start.ExecutorId,
        RuntimeInstanceId=Guid.NewGuid().ToString("D"),ProfileDigest=start.ProfileDigest,InventorySha256=start.MeasuredInventory.Sha256,
        ExecutorBuildSha256=knownProfile!.ExecutorBuildSha256,Lane=BattleExecutorLane.RecoveryDelivery,WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,
        BattleId=start.BattleId,Attempt=start.Attempt,Fence=start.Fence,StartDigest=Google.Protobuf.ByteString.CopyFrom(Rimlink.Protocol.BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray())),
        AssignmentTransitionId=start.PreparationTransitionId},executorBoot);
    internal async Task EnsureApprovedProfile()
    {
        string expected = Convert.ToHexStringLower(ExpectedProfile);
        var records = await H.Db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildBattleProfileApproved").Select(o => o.Receipt).ToArrayAsync();
        if (records.Select(GuildAuthorityEvidence.ReadProfileApproval).Any(r => r.ProfileDigest == expected)) return;
        var approval = await Service.ApproveBattleProfileAsync(Actor.Api,await GuildProfileApprovalTests.Stage(this),default);
        UseExecutorProfile(Convert.FromHexString(approval.ProfileDigest),Rimlink.Protocol.BattleManifestContracts.ReadProfile(Profiles.ReadHeld(approval.Request.Profile)));
    }
    internal GuildMutationScope Scope { get; private set; } = null!;
    internal OutpostTileClaims Tiles { get; }
    private GuildFixture(ManifestFixture manifest) { this.manifest = manifest; Clock=new(Time); Tiles = new(H.Db, H.Authority); }

    internal static async Task<GuildFixture> Create(bool twoGuilds = false,bool separateCommander = false)
    {
        var f = new GuildFixture(await ManifestFixture.Create());
        f.H.Db.Add(new Account { Id = 1, Login = "leader" });
        f.H.Db.Add(new Player { Id = 1, AccountId = 1, Name = "leader", FirstSaveAt = f.Time.Now.UtcDateTime.AddDays(-10) });
        f.H.Db.Add(new Account { Id = 2, Login = "other" });
        f.H.Db.Add(new Player { Id = 2, AccountId = 2, Name = "other", FirstSaveAt = twoGuilds ? f.Time.Now.UtcDateTime.AddDays(-10) : null });
        if (twoGuilds)
        {
            f.H.Db.Add(new WorldObject { OwnerPlayerId = 2, Type = WorldObjectTypes.Settlement, Tile = 16 });
            f.H.Db.Add(new SaveVersion { PlayerId = 2, Number = 1, IsCurrent = true, CreatedAt = f.Time.Now.UtcDateTime.AddDays(-10) });
            f.H.Db.Add(new AllianceGroup { Id = 60, Name = "attackers", OwnerId = 2 });
            f.H.Db.Add(new AllianceMember { PlayerId = 2, AllianceId = 60, JoinedAt = f.Time.Now.UtcDateTime.AddDays(-10) });
        }
        if(separateCommander)
        {
            f.H.Db.Add(new Account{Id=3,Login="commander"});f.H.Db.Add(new Player{Id=3,AccountId=3,Name="commander",FirstSaveAt=f.Time.Now.UtcDateTime.AddDays(-10)});
            f.H.Db.Add(new WorldObject{OwnerPlayerId=3,Type=WorldObjectTypes.Settlement,Tile=15});
            f.H.Db.Add(new SaveVersion{PlayerId=3,Number=1,IsCurrent=true,CreatedAt=f.Time.Now.UtcDateTime.AddDays(-10)});
            f.H.Db.Add(new AllianceMember{PlayerId=3,AllianceId=60,JoinedAt=f.Time.Now.UtcDateTime.AddDays(-10)});
        }
        f.H.Db.Add(new WorldObject { OwnerPlayerId = 1, Type = WorldObjectTypes.Settlement, Tile = 17 });
        f.H.Db.Add(new SaveVersion { PlayerId = 1, Number = 1, IsCurrent = true, CreatedAt = f.Time.Now.UtcDateTime.AddDays(-10) });
        f.H.Db.Add(new AllianceGroup { Id = 50, Name = "guild", OwnerId = 1 });
        f.H.Db.Add(new AllianceMember { PlayerId = 1, AllianceId = 50, JoinedAt = f.Time.Now.UtcDateTime.AddDays(-10) });
        await f.H.Db.SaveChangesAsync(); f.H.Db.ChangeTracker.Clear();
        var document = await f.manifest.Document(); document.TileCount = 18; document.Tiles.Clear();
        for (int tile = 0; tile < 18; tile++) document.Tiles.Add(new OutpostManifestTile { Tile = tile, SurfaceId = "planet", BiomeDef = "TemperateForest", Land = true, CaravanPassable = true, NaturalTrees = true });
        var staged = await f.manifest.Service.StageAsync(Actor.Api, document, default);
        await f.manifest.Service.ConfirmAsync(Actor.Api, staged.ManifestId, staged.Digest, default);
        var admin = OutpostAdminActivationTests.Service(f.H);
        await admin.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(f.H), default);
        await OutpostMainObservationTests.Prepare(f.H, f.H.MainObservation);
        var next = await admin.PreviewActivateGuildDomainFormatAsync(Actor.Api, Guid.NewGuid().ToString("D"), "guild fixture", default);
        await admin.ActivateDomainFormatAsync(Actor.Api, next.Command! with { Confirmed = true }, default);
        var root = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync(); f.Scope = new(root.WorldId, root.RestoreBoundary);
        var economyOptions = Options.Create(new EconomyOptions());
        var economy = new EconomyGate(economyOptions, new FreezeState(), new PlayerFreeze(f.H.Db, null!));
        var raids = Options.Create(new RaidOptions());
        var policy = new GuildCapturePolicy(f.H.Db, economy, new EconomyGuard(f.H.Db, economyOptions), new RaidGate(raids, new RaidFreezeState()), raids);
        var events = new EventBus(f.Sessions, NullLogger<EventBus>.Instance);
        var mailbox = new MailboxService(f.H.Db, events, new WalletService(f.H.Db, events), economyOptions, f.H.Files);
        f.Executors = new(f.Time, "arbiter-1", new byte[32]);
        f.Profiles = new(f.H.Data.Archive);
        f.Artifacts = new(f.H.Data.Archive);
        f.CleanupChallenges = new(f.Time);
        f.serviceFactory = (executor,authority,clock) => new(f.H.Db, authority, new LiveStateLock(), clock, new OutpostManifestService(f.H.Db,authority,clock,f.H.Files), new OutpostTileClaims(f.H.Db,authority), policy, mailbox, executor, f.Profiles, f.Artifacts, f.Battles, f.Sessions,
            new BattleResultStaging(executor,new BattleTransportAdmission(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Artifacts),f.CleanupChallenges,f.LocalWitness);
        f.Service = f.serviceFactory(f.Executors,f.H.Authority,f.Clock);
        return f;
    }

    internal GuildOutpostService ForAuthority(OutpostAuthority authority,OutpostClock clock)=>serviceFactory(Executors,authority,clock);
    internal GuildOutpostService ForExecutor(BattleExecutorRegistry executors)=>serviceFactory(executors,H.Authority,Clock);
    internal Task<GuildOutpostResult> Place(int tile) => Service.PlaceAsync(Actor.Api, Scope, Guid.NewGuid().ToString("D"), tile, GuildResource.Steel, "test placement", default);
    public void Dispose() => manifest.Dispose();
}
