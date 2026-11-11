using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattlePreparedViewTests
{
    [Fact]
    public async Task The_prepared_commander_can_discover_only_the_approved_viewer_documents_before_saving_home()
    {
        using var f = await GuildFixture.Create(true);
        var (start, manifest) = await GuildBattleStartTests.Prepare(f, false);
        var service = new BattlePreparedViewService(new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Artifacts,f.Profiles,f.Executors,f.Sessions,f.Clock);
        var scope = new GuildWorldScope { WorldInstanceId = start.Scope.WorldInstanceId, RestoreBoundary = start.Scope.RestoreBoundary };
        var attacker = f.Sessions.ByPlayer(manifest.Attacker.PlayerId)!;
        var entry = await service.ReadEntryAsync(attacker, scope, default);
        Assert.NotNull(entry);
        Assert.Equal(start.Scope, entry.Value.Entry.Scope);
        Assert.Equal(manifest.ProfileDigest, entry.Value.Entry.ProfileDigest);
        var approval = await GuildAuthorityEvidence.ProfileApprovalAsync(f.H.Db, manifest.ApprovalTransitionId, default);
        Assert.Equal(f.Profiles.ReadHeld(approval.Request.Profile), entry.Value.Entry.Profile.ToByteArray());
        var profile = Rimlink.Protocol.BattleManifestContracts.ReadProfile(entry.Value.Entry.Profile.Span);
        Assert.Equal(f.Profiles.ReadHeld(f.Profiles.Reference(profile.Inventory)), entry.Value.Inventory);
        Assert.Equal(f.Profiles.ReadHeld(f.Profiles.Reference(profile.ViewerCompatibility)), entry.Value.Entry.ViewerCompatibility.ToByteArray());
        var other = f.Sessions.Start(await f.H.Db.Accounts.SingleAsync(a => a.Id == 1), await f.H.Db.Players.SingleAsync(p => p.Id == 1), "127.0.0.1", out _);
        other.GuildVersion = 1;
        Assert.Null(await service.ReadEntryAsync(other, scope, default));
        var stale = scope.Clone();
        stale.RestoreBoundary = Guid.NewGuid().ToString("D");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadEntryAsync(attacker, stale, default));
    }

    [Theory]
    [InlineData(1, BattleFighterSide.Defender)]
    [InlineData(2, BattleFighterSide.Attacker)]
    [InlineData(1, BattleFighterSide.Unspecified)]
    public async Task A_commander_can_join_the_committed_battle_from_an_exact_home(long playerId, BattleFighterSide side)
    {
        using var f = await GuildFixture.Create(true);
        var (request, manifest) = await GuildBattleStartTests.Prepare(f, true);
        if (side != BattleFighterSide.Unspecified)
            await f.Service.AssignDefenderCommanderAsync(1, f.Scope, Guid.NewGuid().ToString("D"), manifest.CampaignId, 1, default);
        var started = await f.Service.StartBattleAsync(request, default);
        var account = await f.H.Db.Accounts.SingleAsync(a => a.Id == playerId);
        var player = await f.H.Db.Players.SingleAsync(p => p.Id == playerId);
        var session = f.Sessions.Start(account, player, "127.0.0.1", out _);
        session.GuildVersion = 1;
        var metadata = GuildSnapshotTests.Metadata(f);
        metadata.Scope.OwnerPlayerId = playerId;
        metadata.GuildMetadata.Scope.PlayerId = playerId;
        var accepted = await GuildSnapshotTests.Accept(f, new(f.H.Db, f.H.Files), metadata, 2, playerId);
        var service = new BattlePreparedViewService(new(f.H.Data, f.H.Files, f.H.Authority, f.H.Feature, f.Battles),
            f.Artifacts, f.Profiles, f.Executors, f.Sessions, f.Clock);
        var entry = await service.ReadEntryAsync(session, new()
            { WorldInstanceId = request.Scope.WorldInstanceId, RestoreBoundary = request.Scope.RestoreBoundary }, default);
        var completed = new BattleCompletedViewRequest { Scope = request.Scope, StartDigest = started.ManifestDigest };
        var transport = BattleRuntimeStartedTests.Transport(f);
        if (side == BattleFighterSide.Unspecified)
        {
            Assert.Null(entry);
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => transport.ReadCompletedViewAsync(session, completed, default));
            return;
        }
        Assert.NotNull(entry);
        Assert.Null((await transport.ReadCompletedViewAsync(session, completed, default)).Receipt);
        Assert.Equal(request.Scope, entry.Value.Entry.Scope);
        var viewRequest = new BattlePreparedViewRequest
        {
            Scope = request.Scope,
            HomeVersion = 2,
            ProfileDigest = manifest.ProfileDigest,
            Home = new() { Scope = metadata.GuildMetadata.Scope, SnapshotId = accepted.Version.SnapshotId,
                RawSha256 = ByteString.CopyFrom(accepted.Version.RawSha256!) },
        };
        var view = await service.ReadAsync(session, viewRequest, default);
        Assert.True(view.StartCommitted);
        Assert.Equal(side, view.Side);
        Assert.Equal(started.Manifest, view.Manifest);
        Assert.Equal(started.ManifestDigest, view.ManifestDigest);
        var initial = await service.ReadInitialAsync(session, new() { View = viewRequest, ManifestDigest = view.ManifestDigest }, default);
        Assert.True(initial.Complete);
        Assert.Equal(view.InitialSnapshot.SizeBytes, initial.Data.Length);
    }

    [Fact]
    public async Task An_initial_page_rechecks_admission_without_reopening_the_complete_template()
    {
        using var f=await GuildFixture.Create(true);var(start,manifest)=await GuildBattleStartTests.Prepare(f,false);
        var metadata=GuildSnapshotTests.Metadata(f);metadata.Scope.OwnerPlayerId=2;metadata.GuildMetadata.Scope.PlayerId=2;
        var home=await GuildSnapshotTests.Accept(f,new(f.H.Db,f.H.Files),metadata,2,2);
        var service=new BattlePreparedViewService(new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Artifacts,f.Profiles,f.Executors,f.Sessions,f.Clock);
        var session=f.Sessions.ByPlayer(2)!;
        var request=new BattlePreparedViewRequest{Scope=start.Scope,HomeVersion=2,ProfileDigest=manifest.ProfileDigest,
            Home=new(){Scope=metadata.GuildMetadata.Scope,SnapshotId=home.Version.SnapshotId,RawSha256=ByteString.CopyFrom(home.Version.RawSha256!)}};
        var view=await service.ReadAsync(session,request,default);
        var approval=await GuildAuthorityEvidence.ProfileApprovalAsync(f.H.Db,manifest.ApprovalTransitionId,default);
        var profile=Rimlink.Protocol.BattleManifestContracts.ReadProfile(f.Profiles.ReadHeld(approval.Request.Profile));
        string template=Path.Combine(f.H.Data.Archive,f.Profiles.Reference(profile.Template).RelativePath.Replace('/',Path.DirectorySeparatorChar));
        using var held=new FileStream(template,FileMode.Open,FileAccess.Read,FileShare.None);
        var page=await service.ReadInitialAsync(session,new(){View=request,ManifestDigest=view.ManifestDigest},default);
        Assert.True(page.Complete);
    }
    [Theory]
    [InlineData("valid")][InlineData("player")][InlineData("home-hash")][InlineData("home-version")][InlineData("generation")]
    [InlineData("profile")][InlineData("fence")][InlineData("session")][InlineData("membership")][InlineData("page-digest")][InlineData("offset")]
    public async Task Only_the_current_prepared_commander_with_an_accepted_exact_home_can_read_bounded_initial_bytes(string fault)
    {
        using var f=await GuildFixture.Create(true);var(start,manifest)=await GuildBattleStartTests.Prepare(f,false);
        var metadata=GuildSnapshotTests.Metadata(f);metadata.Scope.OwnerPlayerId=2;metadata.GuildMetadata.Scope.PlayerId=2;
        var home=await GuildSnapshotTests.Accept(f,new(f.H.Db,f.H.Files),metadata,2,2);
        var session=f.Sessions.ByPlayer(2)!;
        var request=new BattlePreparedViewRequest{Scope=start.Scope.Clone(),HomeVersion=2,ProfileDigest=manifest.ProfileDigest,
            Home=new(){Scope=metadata.GuildMetadata.Scope.Clone(),SnapshotId=home.Version.SnapshotId,RawSha256=ByteString.CopyFrom(home.Version.RawSha256!)}};
        var service=new BattlePreparedViewService(new(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles),f.Artifacts,f.Profiles,f.Executors,f.Sessions,f.Clock);
        switch(fault)
        {
            case "player":request.Home.Scope.PlayerId=1;break;case "home-hash":request.Home.RawSha256=ByteString.CopyFrom(new byte[32]);break;
            case "home-version":request.HomeVersion++;break;case "generation":request.Home.Scope.SaveGeneration++;break;
            case "profile":request.ProfileDigest=ByteString.CopyFrom(new byte[32]);break;case "fence":request.Scope.Fence++;break;
            case "session":f.Sessions.Start(await f.H.Db.Accounts.SingleAsync(a=>a.Id==2),await f.H.Db.Players.SingleAsync(p=>p.Id==2),"127.0.0.1",out _);break;
            case "membership":await f.H.Db.Set<Rimlink.Server.Storage.Entities.AllianceMember>().Where(m=>m.PlayerId==2).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.JoinedAt,f.Time.Now.UtcDateTime));break;
        }
        string head=(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        if(fault is not("valid" or "page-digest" or "offset")){await Assert.ThrowsAnyAsync<Exception>(()=>service.ReadAsync(session,request,default));return;}
        using(await f.H.Anchor.AcquireAsync(default))
        {
            var view=await service.ReadAsync(session,request,default);Assert.Equal(manifest.ToByteString(),view.Manifest);Assert.Equal(BattleFighterSide.Attacker,view.Side);
            var page=new BattlePreparedInitialRequest{View=request,ManifestDigest=view.ManifestDigest,Offset=fault=="offset"?-1:0};
            if(fault=="page-digest")page.ManifestDigest=ByteString.CopyFrom(new byte[32]);
            if(fault!="valid")await Assert.ThrowsAsync<InvalidDataException>(()=>service.ReadInitialAsync(session,page,default));
            else{var reply=await service.ReadInitialAsync(session,page,default);Assert.True(reply.Complete);Assert.Equal(view.InitialSnapshot.SizeBytes,reply.Data.Length);Assert.True(reply.Data.Length<=BattleArtifactStore.ChunkBytes);}
        }
        Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
    }
}
