using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildBattleReadyTests
{
    [Fact]
    public async Task Ready_binds_the_exact_proposal_and_creates_one_grant_without_starting_or_repeating_history()
    {
        using var f = await GuildFixture.Create(true); var (service,executor,manifest) = await BattlePreparedAdmissionTests.Prepare(f);
        var proposed = await service.ProposeAsync(BattlePreparedAdmissionTests.Request(executor,manifest),default);
        var request = Request(manifest,proposed); var session = f.Sessions.ByPlayer(2)!;
        var first = await f.Service.AttackerReadyAsync(session,request,default);
        Assert.Equal(first,await f.Service.AttackerReadyAsync(session,request,default));
        var grant = Assert.Single(await f.H.Db.Set<GuildBattleControlGrant>().AsNoTracking().ToArrayAsync());
        Assert.Equal(first.GrantId,grant.Id); Assert.True(grant.Active); Assert.Equal(2,grant.PlayerId); Assert.Equal(1,grant.Side);
        Assert.Equal(request.CapabilityDigest.ToByteArray(),grant.CapabilityDigest);
        Assert.Null((await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync()).StartDigest);
        request.ClientRequestId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.AttackerReadyAsync(session,request,default));
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
    }
    [Theory]
    [InlineData("digest")][InlineData("fence")][InlineData("deadline")][InlineData("session")][InlineData("superseded")][InlineData("capability")]
    public async Task Stale_role_proposal_or_deadline_cannot_create_ready(string damage)
    {
        using var f = await GuildFixture.Create(true); var (service,executor,manifest) = await BattlePreparedAdmissionTests.Prepare(f);
        var proposed = await service.ProposeAsync(BattlePreparedAdmissionTests.Request(executor,manifest),default);
        var request = Request(manifest,proposed); var session = f.Sessions.ByPlayer(2)!;
        switch (damage) {
            case "digest": request.ManifestDigest = ByteString.CopyFrom(new byte[32]); break;
            case "fence": request.Scope.Fence++; break;
            case "deadline": f.Time.Advance(TimeSpan.FromMinutes(5)); break;
            case "session": f.Sessions.End(session.Token); break;
            case "superseded": manifest.ProposalSequence = 2; manifest.Attacker.ControlEpoch = 2; manifest.Attacker.ControlGrantId = Guid.NewGuid().ToString("D");
                await service.ProposeAsync(BattlePreparedAdmissionTests.Request(executor,manifest),default); break;
            case "capability": request.CapabilityDigest = ByteString.Empty; break;
        }
        var error = await Record.ExceptionAsync(() => f.Service.AttackerReadyAsync(session,request,default));
        Assert.True(error is InvalidDataException or OutpostEvidenceUnavailableException,error?.ToString() ?? "Ready unexpectedly accepted");
        Assert.Empty(await f.H.Db.Set<GuildBattleControlGrant>().ToArrayAsync());
    }
    [Theory]
    [InlineData("BeforePending",false)][InlineData("AfterPending",false)][InlineData("AfterDomainCommit",true)][InlineData("AfterExternalCommit",true)]
    public async Task Lost_ready_reply_reconciles_the_same_grant_and_server_timestamp(string point,bool committed)
    {
        using var f = await GuildFixture.Create(true); var (service,executor,manifest) = await BattlePreparedAdmissionTests.Prepare(f);
        var proposed = await service.ProposeAsync(BattlePreparedAdmissionTests.Request(executor,manifest),default); var request = Request(manifest,proposed);
        f.H.Authority.CommitCheckpoint = checkpoint => { if (checkpoint == point) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => f.Service.AttackerReadyAsync(f.Sessions.ByPlayer(2)!,request,default));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready,(await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed ? 1 : 0,await f.H.Db.Set<GuildBattleControlGrant>().CountAsync());
        var replay = await f.Service.AttackerReadyAsync(f.Sessions.ByPlayer(2)!,request,default);
        Assert.Single(await f.H.Db.Set<GuildBattleControlGrant>().ToArrayAsync());
        Assert.Equal(replay,await f.Service.AttackerReadyAsync(f.Sessions.ByPlayer(2)!,request,default));
    }
    [Fact]
    public async Task A_relogin_cannot_reuse_an_old_ready_request_even_when_the_request_id_is_exact()
    {
        using var f = await GuildFixture.Create(true); var (service,executor,manifest) = await BattlePreparedAdmissionTests.Prepare(f);
        var proposed = await service.ProposeAsync(BattlePreparedAdmissionTests.Request(executor,manifest),default);
        var request = Request(manifest,proposed); var old = f.Sessions.ByPlayer(2)!;
        await f.Service.AttackerReadyAsync(old,request,default);
        f.Sessions.End(old.Token);
        var current = f.Sessions.Start(await f.H.Db.Accounts.SingleAsync(a => a.Id == 2),await f.H.Db.Players.SingleAsync(v => v.Id == 2),"127.0.0.1",out _);
        current.GuildVersion = 1;
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.AttackerReadyAsync(current,request,default));
        Assert.Single(await f.H.Db.Set<GuildBattleControlGrant>().ToArrayAsync());
    }
    private sealed class Interrupted : Exception;
    internal static BattleAttackerReadyRequest Request(BattlePreparedManifestV1 manifest,BattlePreparedProposalReply proposal) => new() {
        ClientRequestId = Guid.NewGuid().ToString("D"),Scope = proposal.Artifact.Scope.Clone(),ProposalSequence = proposal.ProposalSequence,
        ManifestDigest = proposal.ManifestDigest,CapabilityDigest = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData("fixture-capability"u8)) };
}
