using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Backup;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;
using Xunit;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.Auth;
using Rimlink.Protocol;
using Rimlink.Server.Tests.Support;
using Rimlink.GuildAcceptanceHost;
using System.Security.Cryptography;
using Rimlink.Server.Application.Live;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildAcceptanceBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_host_finishes_world_binding_without_rechecking_the_live_authority_each_second(bool alreadyBound)
    {
        using var f = await GuildFixture.Create();
        var admission = new Admission { Grant = new(Guid.NewGuid().ToString("D"), new('b', 64), new('a', 64),
            f.Scope.WorldId, f.Scope.Boundary, GuildAcceptanceScope.Home) };
        var feature = new OutpostFeature(Options.Create(new OutpostOptions()), new Common(), guildAdmission: admission);
        var authority = new OutpostAuthority(f.H.Db, f.H.Anchor, f.H.Files, f.H.Owners, feature);
        await authority.OpenAsync(default);
        if (alreadyBound)
        {
            await authority.BindGuildAcceptanceAsync(default);
            admission.Grant = admission.Grant with { Scope = GuildAcceptanceScope.Slice };
        }
        using var services = new ServiceCollection().AddSingleton(feature).AddSingleton(authority).AddSingleton(f.H.Db).BuildServiceProvider();
        var repeated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var service = new GuildStandBindingService(services.GetRequiredService<IServiceScopeFactory>(), (world, boundary) =>
        {
            Assert.Equal(f.Scope.WorldId, world);
            Assert.Equal(f.Scope.Boundary, boundary);
            if (Interlocked.Increment(ref calls) > 1) repeated.TrySetResult();
        });
        await service.StartAsync(default);
        try
        {
            var finished = await Task.WhenAny(service.ExecuteTask!, repeated.Task).WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Same(service.ExecuteTask, finished);
            await finished;
            Assert.Equal(1, calls);
            Assert.Equal(admission.Grant.StandId, feature.GuildCut!.Acceptance!.StandId);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Qualified_home_login_for_a_player_with_guild_history_waits_for_the_published_cut()
    {
        using var f=await GuildFixture.Create();
        string stand=Guid.NewGuid().ToString("D");
        var admission=new Admission{StandId=stand,Grant=new(stand,new string('b',64),new string('a',64),f.Scope.WorldId,f.Scope.Boundary,GuildAcceptanceScope.Home)};
        var feature=new OutpostFeature(Options.Create(new OutpostOptions()),new Common(),guildAdmission:admission);
        var authority=new OutpostAuthority(f.H.Db,f.H.Anchor,f.H.Files,f.H.Owners,feature);
        await authority.OpenAsync(default);await authority.BindGuildAcceptanceAsync(default);
        await GuildSnapshotTests.Accept(f,new(f.H.Db,f.H.Files),GuildSnapshotTests.Metadata(f),2);
        var account=await f.H.Db.Accounts.SingleAsync(a=>a.Id==1);
        account.PasswordHash=PasswordHasher.Hash("test-password");await f.H.Db.SaveChangesAsync();
        using var server=new ServerFixture().WithoutWorld().WithDataDir(f.H.Data.Root).KeepDataDir()
            .WithSetting("Auth:AllowPassword","true").WithSetting("Outposts:StorageContract","sqlite-local-v1")
            .WithSetting("Outposts:AnchorDirectory",Path.GetDirectoryName(f.H.Anchor.DatabasePath))
            .WithServices(services=>
            {
                services.AddSingleton<IOutpostBuildQualification>(new Common());
                services.AddSingleton<IGuildRuntimeAdmission>(admission);
                services.AddSingleton(f.H.Files);services.AddSingleton(f.H.Owners);services.AddSingleton(f.H.Anchor);
            });
        using var channel=server.CreateChannel();
        var api=new Protocol.V1.Auth.AuthClient(channel);
        var request=new LoginRequest{Password=new(){Login="leader",Password="test-password"},ProtocolVersion=ProtocolVersion.Current,
            RecoveryVersion=SaveRecoveryData.CurrentVersion,OutpostVersion=OutpostRecoveryData.CurrentVersion,
            GuildRuntime=new(){Mode=GuildRuntimeMode.IsolatedAcceptanceV1,StandId=stand}};
        var outdated=await Assert.ThrowsAsync<RpcException>(()=>api.LoginAsync(request).ResponseAsync);
        Assert.Equal(ErrorCode.RecoveryClientRequired,outdated.ErrorDetail()!.Code);
        request.GuildVersion=GuildResourceContracts.CurrentVersion;

        var runtime=server.Services.GetRequiredService<OutpostFeature>();var published=runtime.GuildCut;
        Assert.NotNull(published);
        Task<LoginReply> pending;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using(await f.H.Files.PerStoreLock.HoldAsync(timeout.Token))
        {
            runtime.GuildCut=null;
            try
            {
                pending=api.LoginAsync(request,cancellationToken:timeout.Token).ResponseAsync;
                await Task.WhenAny(pending,Task.Delay(500));Assert.False(pending.IsCompleted);
            }
            finally{runtime.GuildCut=published;}
        }
        var login=await pending;
        Assert.Equal(Role.Player,login.Role);
        Assert.True(login.HasSave);
        Assert.Equal(GuildResourceContracts.CurrentVersion,login.GuildVersion);
        Assert.Equal(f.Scope.Boundary,login.OutpostRestoreBoundary);
        Assert.Equal(request.GuildRuntime,login.GuildRuntime);

        var grant=admission.Grant;
        admission.Grant=null;
        try
        {
            var refused=await Assert.ThrowsAsync<RpcException>(()=>api.LoginAsync(request).ResponseAsync);
            Assert.Equal(ErrorCode.RecoveryRequired,refused.ErrorDetail()!.Code);
            Assert.Equal(login.SessionToken,server.Services.GetRequiredService<SessionRegistry>().ByPlayer(login.Player.PlayerId)!.Token);
        }
        finally{admission.Grant=grant;}
    }

    [Fact]
    public async Task Prepared_initial_RPC_waits_for_the_writers_cut_before_checking_its_battle_and_home()
    {
        using var f=await GuildFixture.Create();var admission=new Admission{Grant=new(Guid.NewGuid().ToString("D"),new string('b',64),new string('a',64),f.Scope.WorldId,f.Scope.Boundary,GuildAcceptanceScope.Home)};
        var feature=new OutpostFeature(Options.Create(new OutpostOptions()),new Common(),guildAdmission:admission);
        var authority=new OutpostAuthority(f.H.Db,f.H.Anchor,f.H.Files,f.H.Owners,feature);
        await authority.OpenAsync(default);await authority.BindGuildAcceptanceAsync(default);
        var views=new BattlePreparedViewService(new(f.H.Data,f.H.Files,authority,feature,f.Battles),f.Artifacts,f.Profiles,f.Executors,f.Sessions,f.Clock);
        using var server=new ServerFixture().WithSetting("Auth:AllowPassword","true").WithServices(services=>
            services.AddScoped<GuildCaptureGrpcService>(_=>new(f.ForAuthority(authority,f.Clock),authority,null!,null!,views)));
        var login=await server.RegisterAndLogin("initial-cut-test");
        server.Services.GetRequiredService<SessionRegistry>().ByPlayer(login.Player.PlayerId)!.GuildVersion=1;
        var request=new BattlePreparedInitialRequest{ManifestDigest=ByteString.CopyFrom(new byte[32]),View=new(){HomeVersion=1,ProfileDigest=ByteString.CopyFrom(new byte[32]),
            Scope=new(){WorldInstanceId=f.Scope.WorldId,RestoreBoundary=f.Scope.Boundary,BattleId=Guid.NewGuid().ToString("D"),Attempt=1,Fence=1},
            Home=new(){SnapshotId=Guid.NewGuid().ToString("N"),RawSha256=ByteString.CopyFrom(new byte[32]),Scope=new(){WorldInstanceId=f.Scope.WorldId,RestoreBoundary=f.Scope.Boundary,PlayerId=login.Player.PlayerId}}}};
        var api=new Protocol.V1.GuildCapture.GuildCaptureClient(server.CreateChannel());Task<BattlePreparedInitialReply> pending;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var published=feature.GuildCut;
        using(await f.H.Files.PerStoreLock.HoldAsync(default))
        {
            feature.GuildCut=null;
            try
            {
                pending=api.ReadPreparedInitialAsync(request,GrpcTestExtensions.Bearer(login.SessionToken),cancellationToken:timeout.Token).ResponseAsync;
                await Task.WhenAny(pending,Task.Delay(500));Assert.False(pending.IsCompleted);
            }
            finally {feature.GuildCut=published;}
        }
        var refused=await Assert.ThrowsAsync<RpcException>(()=>pending);
        Assert.Equal(ErrorCode.GuildUnavailable,refused.ErrorDetail()!.Code); // Waiting never admits the deliberately unknown battle.
    }
    [Fact]
    public async Task An_unpublished_cut_during_a_protected_commit_is_retryable_without_admitting_executor_transport()
    {
        using var f=await GuildFixture.Create();var admission=new Admission{Grant=new(Guid.NewGuid().ToString("D"),new string('b',64),new string('a',64),f.Scope.WorldId,f.Scope.Boundary,GuildAcceptanceScope.Home)};
        var feature=new OutpostFeature(Options.Create(new OutpostOptions()),new Common(),guildAdmission:admission);
        var authority=new OutpostAuthority(f.H.Db,f.H.Anchor,f.H.Files,f.H.Owners,feature);
        await authority.OpenAsync(default);await authority.BindGuildAcceptanceAsync(default);
        var config=new GuildCaptureOptions{ExecutorId="owned-executor",ExecutorTokenSha256=new('c',64),ApprovedProfileDigest=new('d',64)};
        Rimlink.Server.Grpc.BattleExecutorAuthentication.RequireTransport(config,feature);
        bool observed=false;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var transport=new BattleTransportAdmission(f.H.Data,f.H.Files,authority,feature,f.Battles);
        Task<BattleTransportAdmission.Lease>? pending=null;
        authority.CommitCheckpoint=point=>
        {
            if(point!="BeforePending")return;observed=true;Assert.Null(feature.GuildCut);
            var refusal=Assert.Throws<global::Grpc.Core.RpcException>(()=>Rimlink.Server.Grpc.BattleExecutorAuthentication.RequireTransport(config,feature));
            Assert.Equal(global::Grpc.Core.StatusCode.Unavailable,refusal.StatusCode);
            pending=transport.HoldAsync(deadline.Token);Assert.False(pending.IsCompleted);
        };
        await f.ForAuthority(authority,f.Clock).PlaceAsync(Rimlink.Server.Application.Admin.Actor.Api,f.Scope,Guid.NewGuid().ToString("D"),0,GuildResource.Steel,"qualified fixture placement",default);
        Assert.True(observed);Assert.NotNull(feature.GuildCut);
        await using var admitted=await pending!;Assert.Equal(feature.GuildCut,admitted.Cut);
        Rimlink.Server.Grpc.BattleExecutorAuthentication.RequireTransport(config,feature);
    }
    private sealed class Common:IOutpostBuildQualification
    {public OutpostBuildQualification Observe()=>new(OutpostRuntimeMode.IsolatedAcceptance,OutpostQualificationState.Pending,new string('a',64),true,true);}
    private sealed class Admission:IGuildRuntimeAdmission
    {public string? StandId{get;init;} internal GuildRuntimeGrant? Grant;public GuildRuntimeGrant? Observe()=>Grant;}
    [Fact]
    public async Task Acceptance_genesis_binds_the_stand_to_the_common_chain_and_a_missing_provider_cannot_adopt_it()
    {
        using var f=await GuildFixture.Create();var admission=new Admission{Grant=new(Guid.NewGuid().ToString("D"),new string('b',64),new string('a',64),f.Scope.WorldId,f.Scope.Boundary,GuildAcceptanceScope.Home)};
        var feature=new OutpostFeature(Options.Create(new OutpostOptions()),new Common(),guildAdmission:admission);
        var authority=new OutpostAuthority(f.H.Db,f.H.Anchor,f.H.Files,f.H.Owners,feature);
        Assert.Equal(OutpostStartupState.Ready,(await authority.OpenAsync(default)).State);
        Assert.Throws<OutpostEvidenceUnavailableException>(()=>authority.RequireRuntimeGuild());
        var identity=await authority.BindGuildAcceptanceAsync(default);Assert.Equal(admission.Grant.StandId,identity.StandId);Assert.Equal(identity,await authority.BindGuildAcceptanceAsync(default));
        authority.RequireRuntimeGuild();Assert.Equal(identity,feature.GuildCut!.Acceptance);
        var head=await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var observed=await OutpostAuthority.ReadStoredChainAsync(f.H.Db,f.H.Files,head,new(head.HeadId,Google.Protobuf.ByteString.CopyFrom(head.HeadDigest)),false,default);
        var cut=new OutpostBackupManifest(3,new(head.FormatVersion,head.InstallationId,head.WorldId,head.RestoreBoundary,head.HeadId,head.HeadDigest,head.TransitionId!,head.Phase),
            f.Time.Now.UtcDateTime,observed.Digest.ToByteArray(),[],[],new(2,OutpostDomainFormat.V3,new(observed.Schema.ProfileId,observed.Schema.Digest.ToByteArray()),identity));
        Assert.Equal(identity,OutpostBackupManifest.Decode(cut.Encode()).Domain!.Acceptance);
        Assert.Throws<InvalidDataException>(()=>cut.ValidateDomain(observed));
        var stripped=cut with{Domain=cut.Domain! with{Acceptance=null}};Assert.Throws<InvalidDataException>(()=>stripped.ValidateDomain(observed));
        Assert.Throws<OutpostEvidenceUnavailableException>(()=>authority.RequireRuntimeGuild(GuildRuntimeUse.Simulation));
        admission.Grant=null;Assert.Equal(OutpostStartupState.Maintenance,(await authority.OpenAsync(default)).State);
        var ordinary=new OutpostAuthority(f.H.Db,f.H.Anchor,f.H.Files,f.H.Owners,new OutpostFeature(Options.Create(new OutpostOptions()),new Common()));
        Assert.Equal(OutpostStartupState.Maintenance,(await ordinary.OpenAsync(default)).State);
    }
    [Fact]
    public async Task Acceptance_binding_cannot_adopt_an_already_used_guild_domain()
    {
        using var f=await GuildFixture.Create();await f.Place(0);
        var admission=new Admission{Grant=new(Guid.NewGuid().ToString("D"),new string('b',64),new string('a',64),f.Scope.WorldId,f.Scope.Boundary,GuildAcceptanceScope.Home)};
        var feature=new OutpostFeature(Options.Create(new OutpostOptions()),new Common(),guildAdmission:admission);var authority=new OutpostAuthority(f.H.Db,f.H.Anchor,f.H.Files,f.H.Owners,feature);
        await authority.OpenAsync(default);await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(()=>authority.BindGuildAcceptanceAsync(default));
    }

    [Theory]
    [InlineData("AfterJournalCreated")]
    [InlineData("AfterMainDatabaseCommit")]
    public async Task Isolated_restore_requires_the_exact_archive_and_admits_only_the_bound_fresh_target(string stop)
    {
        using var f = await GuildFixture.Create();
        var h = f.H;
        var admission = new Admission { Grant = new(Guid.NewGuid().ToString("D"), new string('b', 64), new string('a', 64),
            f.Scope.WorldId, f.Scope.Boundary, GuildAcceptanceScope.Home) };
        var feature = new OutpostFeature(Options.Create(new OutpostOptions()), new Common(), guildAdmission: admission);
        var authority = new OutpostAuthority(h.Db, h.Anchor, h.Files, h.Owners, feature);
        await authority.OpenAsync(default);
        var source = await authority.BindGuildAcceptanceAsync(default);
        admission.Grant = admission.Grant! with { Scope = GuildAcceptanceScope.Slice };
        var issued = await GuildDeliveryStartTests.Prepared(f);
        var before = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        string archive = Path.Combine(h.Data.Backups, "isolated.tar.zst");
        await new OutpostBackupCapture(h.Db, h.Data, authority, h.Anchor, null!, f.Clock, new LiveStateLock()).CreateAsync(archive, default);
        string data = Directory.CreateDirectory(Path.Combine(h.Root, "isolated-target")).FullName;
        string anchor = Directory.CreateDirectory(Path.Combine(h.Root, "isolated-anchor")).FullName;
        string linux = LocalTestStorage.LinuxQualification(data);
        var store = new OutpostAnchorStore(data, Options.Create(new OutpostOptions
            { AnchorDirectory = anchor, StorageContract = "sqlite-local-v1", QualifiedLinuxFileSystem = linux }));
        store.Provision();

        var target = source with { StandId = Guid.NewGuid().ToString("D"), BootstrapDigest = new string('c', 64), Boundary = Guid.NewGuid().ToString("D") };
        var permit = new GuildAcceptanceRestorePermit(source, target, archive, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archive))),
            data, anchor, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        var ordinary = new OutpostRestoreProcedure(f.Time);
        await Assert.ThrowsAsync<InvalidDataException>(() => ordinary.InspectAsync(archive, data, anchor, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OutpostRestoreProcedure(permit with { ArchiveSha256 = new string('0', 64) }, f.Time).InspectIsolatedAsync(default));
        var wrongSource = permit with { Source = source with { StandId = Guid.NewGuid().ToString("D") } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new OutpostRestoreProcedure(wrongSource, f.Time).InspectIsolatedAsync(default));

        var procedure = new OutpostRestoreProcedure(permit, f.Time);
        var (inspection, _) = await procedure.InspectIsolatedAsync(default);
        var request = new RestoreRequest(permit.RestoreId, RestoreMode.ImportNewHost, inspection, permit.InstallationId, target.Boundary, [], "sqlite-local-v1", linux);
        var unscoped = await procedure.ExecuteAsync(request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) }, default);
        Assert.Equal(RestoreOutcome.Refused, unscoped.Outcome);
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.Digest(OutpostRestoreJournalCodec.Freeze(request, permit)) };
        procedure.Checkpoint = point => { if (point == stop) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => procedure.ExecuteAsync(request, default));
        Assert.Equal(RestoreOutcome.Refused, (await ordinary.ResumeAsync(data, anchor, permit.RestoreId, default)).Outcome);

        procedure = new OutpostRestoreProcedure(permit, f.Time);
        var result = await procedure.ResumeAsync(data, anchor, permit.RestoreId, default);
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
        Assert.Equal(result.HeadId, (await procedure.ResumeAsync(data, anchor, permit.RestoreId, default)).HeadId);
        await using var restoredDb = OutpostRestoreProcedure.OpenRestoreDatabase(data);
        var files = new Rimlink.Server.Application.Saves.SaveFiles(Rimlink.Server.Storage.DataDirectory.Existing(data));
        var root = await restoredDb.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == permit.RestoreId);
        Assert.Equal(permit, OutpostRestoreEvidence.Plan(root).Journal.Selection.Isolated);
        Assert.Equal(source, await GuildAuthorityEvidence.AcceptanceIdentityAsync(restoredDb, default));
        var actual = await restoredDb.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        Assert.Equal(before.SnapshotId, actual.SnapshotId);
        Assert.Equal(before.RawSha256, actual.RawSha256);
        Assert.Equal(issued.ParcelId, (await restoredDb.Set<Rimlink.Server.Storage.Entities.GuildParcelLink>().AsNoTracking().SingleAsync()).ParcelId);

        var restoredAdmission = new Admission();
        var restoredFeature = new OutpostFeature(Options.Create(new OutpostOptions()), new Common(), guildAdmission: restoredAdmission);
        var restoredAuthority = new OutpostAuthority(restoredDb, store, files, h.Owners, restoredFeature);
        Assert.Equal(OutpostStartupState.Maintenance, (await restoredAuthority.OpenAsync(default)).State);
        restoredAdmission.Grant = new(target.StandId, target.BootstrapDigest, target.SourceIdentity, target.WorldId, target.Boundary, GuildAcceptanceScope.Restore);
        Assert.Equal(OutpostStartupState.Ready, (await restoredAuthority.OpenAsync(default)).State);
        restoredAuthority.RequireRuntimeGuild(GuildRuntimeUse.Economy);
        Assert.Equal(target, restoredFeature.GuildCut!.Acceptance);
    }

    private sealed class Interrupted : Exception;
}
