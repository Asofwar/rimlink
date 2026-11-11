using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildProfileApprovalTests
{
    [Fact]
    public async Task Approval_is_an_exact_common_receipt_for_full_archived_bytes_and_does_not_enable_Product()
    {
        using var f = await GuildFixture.Create(); var request = await Stage(f);
        var receipt = await f.Service.ApproveBattleProfileAsync(Actor.Api,request,default);
        Assert.Equal(receipt,await f.Service.ApproveBattleProfileAsync(Actor.Api,request,default));
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        var approved = await GuildAuthorityEvidence.RequireApprovedProfileAsync(f.H.Db,f.Profiles,receipt.TransitionId,receipt.ExecutorId,Convert.FromHexString(receipt.ProfileDigest),default);
        Assert.Equal(receipt.ProfileId,approved.Profile.ProfileId);
        Assert.Single(await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildCaptureOperation>().Where(o => o.Kind == "GuildBattleProfileApproved").ToArrayAsync());
        Assert.Null(f.Executors.Available());
    }
    [Fact]
    public async Task A_Config_hash_without_full_inventory_and_content_documents_is_not_an_approval()
    {
        using var f = await GuildFixture.Create(); var request = await Stage(f);
        var profile = BattleManifestContracts.ReadProfile(f.Profiles.ReadHeld(request.Profile));
        var inventory = f.Profiles.Reference(profile.Inventory);
        File.Move(Path.Combine(f.H.Data.Archive,inventory.RelativePath),Path.Combine(f.H.Data.Archive,inventory.RelativePath) + ".missing");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.ApproveBattleProfileAsync(Actor.Api,request,default));
        Assert.Empty(await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildCaptureOperation>().Where(o => o.Kind == "GuildBattleProfileApproved").ToArrayAsync());
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Revocation_is_durable_and_replay_does_not_restore_cached_admission(bool clockSteppedBack)
    {
        using var f = await GuildFixture.Create(); var approval = await f.Service.ApproveBattleProfileAsync(Actor.Api,await Stage(f),default);
        string request = Guid.NewGuid().ToString("D");
        if (clockSteppedBack) f.Time.Now = f.Time.Now.AddHours(-1);
        var revoked = await f.Service.RevokeBattleProfileAsync(Actor.Api,f.Scope,request,approval.TransitionId,"replaced build",default);
        Assert.Equal(revoked,await f.Service.RevokeBattleProfileAsync(Actor.Api,f.Scope,request,approval.TransitionId,"replaced build",default));
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => GuildAuthorityEvidence.RequireApprovedProfileAsync(f.H.Db,f.Profiles,
            approval.TransitionId,approval.ExecutorId,Convert.FromHexString(approval.ProfileDigest),default));
    }
    [Theory]
    [InlineData("BeforePending",false)][InlineData("AfterPending",false)][InlineData("AfterDomainCommit",true)][InlineData("AfterExternalCommit",true)]
    public async Task Crash_reconciles_the_same_profile_approval_exactly_once(string checkpoint,bool committed)
    {
        using var f = await GuildFixture.Create(); var request = await Stage(f);
        f.H.Authority.CommitCheckpoint = point => { if (point == checkpoint) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => f.Service.ApproveBattleProfileAsync(Actor.Api,request,default));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed ? 1 : 0,await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildCaptureOperation>().CountAsync(o => o.Kind == "GuildBattleProfileApproved"));
        await f.Service.ApproveBattleProfileAsync(Actor.Api,request,default);
        Assert.Equal(1,await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildCaptureOperation>().CountAsync(o => o.Kind == "GuildBattleProfileApproved"));
    }
    [Fact]
    public async Task Archived_profile_damage_closes_startup_without_replacing_or_approving_the_new_bytes()
    {
        using var f = await GuildFixture.Create(); var request = await Stage(f);
        await f.Service.ApproveBattleProfileAsync(Actor.Api,request,default);
        File.WriteAllText(Path.Combine(f.H.Data.Archive,request.Profile.RelativePath),"damage");
        Assert.NotEqual(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        Assert.Null(f.H.Feature.GuildCut);
    }
    [Fact]
    public async Task A_configured_digest_without_common_approval_cannot_reserve_a_campaign()
    {
        using var f = await GuildFixture.Create(true); var target = await f.Place(0);
        var consent = await f.Service.PreviewConsentAsync(1,50,f.Scope,target.OutpostId,12,default);
        await f.Service.AcceptConsentAsync(1,50,f.Scope,Guid.NewGuid().ToString("D"),target.OutpostId,12,consent.Digest,default);
        f.RegisterExecutor();
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.DeclareCampaignAsync(2,new(f.Scope,Guid.NewGuid().ToString("D"),60,target.OutpostId,1,2),default));
        Assert.Empty(await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildCampaign>().ToArrayAsync());
    }
    [Fact]
    public async Task Profile_revocation_closes_preparation_and_current_grants_without_rewriting_a_started_outcome()
    {
        using var f = await GuildFixture.Create(true); var (request,manifest) = await GuildBattleStartTests.Prepare(f,true);
        var grant = await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildBattleControlGrant>().AsNoTracking().SingleAsync();
        Assert.True(grant.Active);
        await f.Service.RevokeBattleProfileAsync(Actor.Api,f.Scope,Guid.NewGuid().ToString("D"),manifest.ApprovalTransitionId,"withdrawn fixture",default);
        Assert.False((await f.H.Db.Set<Rimlink.Server.Storage.Entities.GuildBattleControlGrant>().AsNoTracking().SingleAsync()).Active);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.StartBattleAsync(request,default));
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    private sealed class Interrupted : Exception;

    internal static async Task<GuildProfileApprovalRequest> Stage(GuildFixture f)
    {
        using var storage = await f.H.Files.PerStoreLock.HoldAsync(default);
        var config = f.Profiles.PutHeld(BattleDocumentKind.Config,"fixture-config"u8);
        var defs = f.Profiles.PutHeld(BattleDocumentKind.Defs,"fixture-defs"u8);
        var template = f.Profiles.PutHeld(BattleDocumentKind.Template,"fixture-template-not-native-proof"u8);
        var viewer = f.Profiles.PutHeld(BattleDocumentKind.ViewerCompatibility,"fixture-viewer"u8);
        var qualification = f.Profiles.PutHeld(BattleDocumentKind.Qualification,"fixture-only-not-Product-qualification"u8);
        ByteString hash = ByteString.CopyFrom(new byte[32]);
        var inventory = new BattleRuntimeInventoryV1 { Version = 1, GameBuildSha256 = hash, RuntimeBuildSha256 = hash,
            ClientBuildSha256 = hash, ExecutorBuildSha256 = hash, Config = config.Descriptor, LoadedDefs = defs.Descriptor };
        inventory.Mods.Add(new BattleModInventoryEntry { PackageId = "rimlink",Version = "0.9.2",RootId = "rimlink" });
        inventory.Files.Add(new BattleFileInventoryEntry { Root = BattleInventoryRoot.Mod,RootId = "rimlink",RelativePath = "Assemblies/Rimlink.Client.dll",Sha256 = hash,SizeBytes = 1 });
        var inventoryDocument = f.Profiles.PutHeld(BattleDocumentKind.Inventory,inventory.ToByteArray());
        var profile = new BattleProfileV1 { Version = 1,ProfileId = Guid.NewGuid().ToString("D"),Revision = 1,Inventory = inventoryDocument.Descriptor,
            ExecutorBuildSha256 = hash,ViewerBuildSha256 = hash,ViewerCompatibility = viewer.Descriptor,Template = template.Descriptor,
            Rules = new() { CaptureTicks = 3000,MaximumTicks = 30000,WallSeconds = 1200,AiAfterSeconds = 30,TransportGraceSeconds = 60 },
            Arena = new() { Width = 50,Height = 50,ZoneMinX = 20,ZoneMinZ = 20,ZoneMaxX = 29,ZoneMaxZ = 29 },
            Limits = new() { CommandBytes = 16384,CommandsPerSecondPerSide = 20,PendingPerSide = 64,SnapshotChunkBytes = 262144,SnapshotBytes = 67108864,FullDeltaFrameBytes = 1048576 },
            SnapshotEncodingVersion = 1,DeltaEncodingVersion = 1 };
        profile.CommandAllowlist.Add(["Move","Attack","Draft"]);
        for (int i = 0;i < 8;i++) profile.Fighters.Add(new BattleFighterSpec { FighterId = "fighter" + i,
            Side = i < 4 ? BattleFighterSide.Attacker : BattleFighterSide.Defender,PawnKindDef = "Colonist",RaceDef = "Human",AgeYears = 25,
            Gender = BattleFighterGender.Male,X = i < 4 ? 10 : 40,Z = 10 + i,WeaponDef = "Gun_BoltActionRifle",Shooting = 8,Melee = 8 });
        profile.RosterDigest = ByteString.CopyFrom(BattleManifestContracts.RosterDigest(profile.Fighters));
        return new(f.Scope,Guid.NewGuid().ToString("D"),f.Profiles.PutHeld(BattleDocumentKind.Profile,profile.ToByteArray()),qualification,"arbiter-1","fixture approval");
    }
}
