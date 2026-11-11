using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildBattleStartTests
{
    [Fact]
    public async Task Start_commits_the_exact_ready_manifest_and_initial_evidence_once_without_awarding_the_object()
    {
        using var f = await GuildFixture.Create(true); var (request,manifest) = await Prepare(f,true);
        var first = await f.Service.StartBattleAsync(request,default);
        Assert.Equal(first,await f.Service.StartBattleAsync(request,default));
        Assert.Equal(manifest.ToByteArray(),first.Manifest.ToByteArray());
        Assert.Equal(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,first.Manifest.Span),first.ManifestDigest.ToByteArray());
        Assert.Equal(32,first.AuthorityHeadDigest.Length);
        var battle = await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync();
        Assert.Equal("StartCommitted",battle.Phase); Assert.True(battle.Active); Assert.NotNull(battle.StartedAtUtc);
        Assert.Equal(first.Manifest.ToByteArray(),battle.StartManifest); Assert.Equal(first.ManifestDigest.ToByteArray(),battle.StartDigest);
        Assert.Equal(2,await f.H.Db.Set<GuildBattleArtifact>().CountAsync());
        var row = await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync();
        Assert.Equal(50,row.AllianceId); Assert.Equal(1,row.Revision); Assert.Equal(manifest.StockAtCut,row.Stock);
        Assert.Empty(await f.H.Db.Set<GuildResourceObligation>().ToArrayAsync());
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Theory]
    [InlineData("not-ready")][InlineData("digest")][InlineData("relogin")][InlineData("deadline")][InlineData("revoked")][InlineData("frozen")][InlineData("initial-damaged")]
    public async Task Invalidated_preconditions_never_authorize_a_tick(string damage)
    {
        using var f = await GuildFixture.Create(true); var (request,manifest) = await Prepare(f,damage != "not-ready");
        switch (damage) {
            case "digest": request.ManifestDigest = ByteString.CopyFrom(new byte[32]); break;
            case "relogin": f.Sessions.End(f.Sessions.ByPlayer(2)!.Token); break;
            case "deadline": f.Time.Advance(TimeSpan.FromMinutes(5)); break;
            case "revoked": await f.Service.RevokeBattleProfileAsync(Actor.Api,f.Scope,Guid.NewGuid().ToString("D"),manifest.ApprovalTransitionId,"withdrawn fixture",default); break;
            case "frozen": var player = await f.H.Db.Players.SingleAsync(p => p.Id == 2); player.EconomyFrozenAt = f.Time.Now.UtcDateTime; await f.H.Db.SaveChangesAsync(); f.H.Db.ChangeTracker.Clear(); break;
            case "initial-damaged": var descriptor = f.Artifacts.DescriptorHeld(manifest.BattleId,1,"initial")!; var path = f.Artifacts.ValidateCompleteHeld(descriptor).RelativePath;
                File.WriteAllText(Path.Combine(f.H.Data.Archive,path),"damaged"); break;
        }
        var error = await Record.ExceptionAsync(() => f.Service.StartBattleAsync(request,default));
        Assert.True(error is InvalidDataException or OutpostEvidenceUnavailableException or global::Grpc.Core.RpcException,error?.ToString() ?? "Start unexpectedly accepted");
        Assert.Null((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).StartDigest);
        Assert.Empty(await f.H.Db.Set<GuildBattleArtifact>().ToArrayAsync());
    }
    [Theory]
    [InlineData("BeforePending",false)][InlineData("AfterPending",false)][InlineData("AfterDomainCommit",true)][InlineData("AfterExternalCommit",true)]
    public async Task Lost_start_authorization_replays_the_same_manifest_started_time_and_head(string point,bool committed)
    {
        using var f = await GuildFixture.Create(true); var (request,manifest) = await Prepare(f,true);
        f.H.Authority.CommitCheckpoint = checkpoint => { if (checkpoint == point) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => f.Service.StartBattleAsync(request,default));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed,(await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).StartedAtUtc is not null);
        var replay = await f.Service.StartBattleAsync(request,default);
        Assert.Equal(replay,await f.Service.StartBattleAsync(request,default));
        Assert.Equal(2,await f.H.Db.Set<GuildBattleArtifact>().CountAsync());
    }
    private sealed class Interrupted : Exception;
    internal static async Task<(BattleStartRequest Request,BattlePreparedManifestV1 Manifest)> Prepare(GuildFixture f,bool ready)
    {
        var (service,executor,manifest) = await BattlePreparedAdmissionTests.Prepare(f);
        var proposed = await service.ProposeAsync(BattlePreparedAdmissionTests.Request(executor,manifest),default);
        if (ready) await f.Service.AttackerReadyAsync(f.Sessions.ByPlayer(2)!,GuildBattleReadyTests.Request(manifest,proposed),default);
        return (new() { ClientRequestId = Guid.NewGuid().ToString("D"),Executor = new() { ExecutorId = executor.ExecutorId,LeaseId = executor.LeaseId,Fence = executor.Fence,
            ProfileDigest = executor.ProfileDigest },Scope = proposed.Artifact.Scope.Clone(),ManifestDigest = proposed.ManifestDigest },manifest);
    }
}
