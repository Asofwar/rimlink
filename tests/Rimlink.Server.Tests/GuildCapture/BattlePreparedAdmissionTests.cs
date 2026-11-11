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

public sealed class BattlePreparedAdmissionTests
{
    [Fact]
    public async Task Proposal_is_exact_durable_transport_and_never_a_financial_start_or_ready()
    {
        using var f = await GuildFixture.Create(true); var (service,executor,manifest) = await Prepare(f);
        string head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        var request = Request(executor,manifest);
        var first = await service.ProposeAsync(request,default);
        Assert.Equal(first,await service.ProposeAsync(request,default));
        Assert.Equal(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,manifest.ToByteArray()),first.ManifestDigest.ToByteArray());
        Assert.Equal(head,(await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        var battle = await f.H.Db.Set<GuildBattle>().AsNoTracking().SingleAsync();
        Assert.Null(battle.StartedAtUtc); Assert.Null(battle.StartManifest); Assert.Null(battle.StartDigest);
        Assert.Empty(await f.H.Db.Set<GuildBattleControlGrant>().ToArrayAsync());
    }
    [Theory]
    [InlineData("session")][InlineData("membership")][InlineData("profile")][InlineData("nonce")][InlineData("cut")][InlineData("initial")][InlineData("deadline")][InlineData("epoch")][InlineData("revoked")]
    public async Task Wrong_runtime_role_or_asset_binding_cannot_publish_ready_enabled_proposal(string change)
    {
        using var f = await GuildFixture.Create(true); var (service,executor,manifest) = await Prepare(f);
        switch (change) {
            case "session": f.Sessions.End(f.Sessions.ByPlayer(2)!.Token); break;
            case "membership": manifest.Attacker.MembershipJoinedUtcTicks++; break;
            case "profile": manifest.ProfileRevision++; break;
            case "nonce": manifest.StartNonce = ByteString.CopyFrom(new byte[32]); break;
            case "cut": manifest.StockAtCut--; break;
            case "initial": manifest.InitialSnapshot.SizeBytes++; break;
            case "deadline": f.Time.Advance(TimeSpan.FromMinutes(5)); f.RegisterExecutor(); break;
            case "epoch": manifest.Attacker.ControlEpoch++; break;
            case "revoked": await f.Service.RevokeBattleProfileAsync(Actor.Api,f.Scope,Guid.NewGuid().ToString("D"),manifest.ApprovalTransitionId,"withdrawn fixture",default); break;
        }
        var error = await Record.ExceptionAsync(() => service.ProposeAsync(Request(executor,manifest),default));
        Assert.True(error is InvalidDataException or OutpostEvidenceUnavailableException,error?.ToString() ?? "proposal unexpectedly accepted");
        Assert.Null(new BattlePreparedJournal(f.Artifacts).LatestHeld(manifest.BattleId,manifest.Fence));
    }
    internal static BattlePreparedProposalRequest Request(BattleExecutorLease executor,BattlePreparedManifestV1 manifest) => new() {
        Executor = new() { ExecutorId = executor.ExecutorId,LeaseId = executor.LeaseId,Fence = executor.Fence,ProfileDigest = executor.ProfileDigest },Manifest = manifest.ToByteString() };
    internal static async Task<(BattlePreparedAdmission Service,BattleExecutorLease Executor,BattlePreparedManifestV1 Manifest)> Prepare(GuildFixture f)
    {
        var approval = await f.Service.ApproveBattleProfileAsync(Actor.Api,await GuildProfileApprovalTests.Stage(f),default);
        f.UseExecutorProfile(Convert.FromHexString(approval.ProfileDigest),BattleManifestContracts.ReadProfile(f.Profiles.ReadHeld(approval.Request.Profile)));
        var (executor,prepared) = await BattleExecutorWorkTests.Prepare(f);
        var campaign = await f.H.Db.Set<GuildCampaign>().AsNoTracking().SingleAsync();
        var scheduled = await GuildAuthorityEvidence.CampaignScheduleAsync(f.H.Db,campaign.Id,default);
        var profile = BattleManifestContracts.ReadProfile(f.Profiles.ReadHeld(approval.Request.Profile));
        var session = f.Sessions.Start(await f.H.Db.Accounts.SingleAsync(a => a.Id == 2),await f.H.Db.Players.SingleAsync(p => p.Id == 2),"127.0.0.1",out _);
        session.GuildVersion = 1;
        var member = await f.H.Db.Set<AllianceMember>().AsNoTracking().SingleAsync(m => m.PlayerId == 2);
        byte[] initial = "fixture-native-initial-placeholder-not-a-game-proof"u8.ToArray();
        using (await f.H.Files.PerStoreLock.HoldAsync(default))
        using (await f.Battles.HoldAsync(default))
            f.Artifacts.AppendHeld(new(1,prepared.BattleId,f.Scope.WorldId,f.Scope.Boundary,"arbiter-1",1,"initial",initial.Length,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(initial))),0,initial);
        var manifest = BattlePreparedJournalTests.Manifest();
        manifest.WorldInstanceId = f.Scope.WorldId; manifest.RestoreBoundary = f.Scope.Boundary; manifest.CampaignId = campaign.Id; manifest.BattleId = prepared.BattleId;
        manifest.OutpostId = campaign.OutpostId; manifest.OwnerRevision = campaign.OwnerRevision; manifest.AttackerAllianceId = campaign.AttackerAllianceId; manifest.DefenderAllianceId = campaign.DefenderAllianceId;
        manifest.RuntimeInstanceId=f.Executors.BindingOf(executor)!.RuntimeInstanceId;
        manifest.ExecutorLeaseId = executor.LeaseId; manifest.ExecutorLeaseFence = executor.Fence; manifest.ProfileId = approval.ProfileId; manifest.ProfileRevision = approval.Revision;
        manifest.ProfileDigest = executor.ProfileDigest; manifest.ApprovalTransitionId = approval.TransitionId; manifest.StartNonce = ByteString.CopyFrom(Convert.FromHexString(prepared.StartNonce));
        manifest.MeasuredInventory = profile.Inventory.Clone(); manifest.RulesDigest = ByteString.CopyFrom(campaign.RulesDigest); manifest.Template = profile.Template.Clone(); manifest.RosterDigest = profile.RosterDigest;
        manifest.InitialSnapshot = new() { Kind = BattleDocumentKind.InitialSnapshot,SizeBytes = initial.Length,Sha256 = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(initial)) };
        manifest.Arena = profile.Arena.Clone(); manifest.Attacker = new() { PlayerId = 2,AllianceId = 60,ControlGrantId = Guid.NewGuid().ToString("D"),SessionDigest = BattleCommanderIdentity.Digest(session),ControlEpoch = 1,MembershipJoinedUtcTicks = member.JoinedAt.Ticks };
        manifest.PreparationTransitionId = prepared.TransitionId; manifest.ProductionCutUtcTicks = prepared.PreparedAtUtc.Ticks; manifest.StockAtCut = prepared.StockAtCut; manifest.RemainderAtCut = prepared.RemainderAtCut;
        manifest.AcquisitionReservationId = scheduled.ReservationId; manifest.SlotId = scheduled.SlotId; manifest.SlotStartUtcTicks = campaign.ScheduledAtUtc.Ticks;
        manifest.SlotEndUtcTicks = campaign.ScheduledAtUtc.AddMinutes(30).Ticks; manifest.ReadinessDeadlineUtcTicks = campaign.ScheduledAtUtc.AddMinutes(5).Ticks;
        var admission = new BattleTransportAdmission(f.H.Data,f.H.Files,f.H.Authority,f.H.Feature,f.Battles);
        var work = new BattleExecutorWorkService(f.Executors,admission,f.Clock,f.Artifacts);
        return (new(f.Executors,admission,work,f.Artifacts,f.Profiles,f.Sessions),executor,manifest);
    }
}
