using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattlePreparationBuilderTests
{
    private static (BattleProfileV1 Profile,BattlePreparationBasisReply Reply,BattlePreparedManifestV1 Complete) Fixture()
    {
        var profile=BattleManifestContractsTests.Profile();var start=BattleManifestContractsTests.Manifest();
        start.ProfileId=profile.ProfileId;start.ProfileDigest=ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.Profile,profile.ToByteArray()));
        var assignment=new BattleRuntimeAssignmentV1{Version=1,Scope=new(){WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,BattleId=start.BattleId,Attempt=start.Attempt,Fence=start.Fence},
            CampaignId=start.CampaignId,ExecutorId=start.ExecutorId,RuntimeInstanceId=start.RuntimeInstanceId,OriginalLeaseId=start.ExecutorLeaseId,OriginalLeaseFence=start.ExecutorLeaseFence,
            StartNonce=start.StartNonce,ApprovalTransitionId=start.ApprovalTransitionId,ProfileDigest=start.ProfileDigest,InventorySha256=start.MeasuredInventory.Sha256,
            ExecutorBuildSha256=profile.ExecutorBuildSha256,AssignedAtUtcTicks=start.ProductionCutUtcTicks,TransitionId=start.PreparationTransitionId};
        var basis=start.Clone();basis.InitialSnapshot=null;basis.InitialTick=0;basis.Entities.Clear();return(profile,new(){Assignment=assignment,Basis=basis},start);
    }
    [Fact]
    public void Measured_native_initial_completes_the_exact_server_basis_without_changing_the_original_assignment()
    {
        var(profile,reply,start)=Fixture();var before=reply.Clone();
        var actual=BattlePreparationBuilder.Build(reply.Assignment,profile,reply,start.InitialSnapshot,start.InitialTick,start.Entities);
        Assert.Equal(start,actual);Assert.Equal(before,reply);
    }
    [Theory]
    [InlineData("assignment")][InlineData("runtime")][InlineData("profile")][InlineData("inventory")][InlineData("cut")][InlineData("grant")][InlineData("roster")][InlineData("native-in-basis")][InlineData("duplicate")]
    public void Mismatched_assignment_profile_or_native_state_cannot_be_published_as_a_prepared_manifest(string damage)
    {
        var(profile,reply,start)=Fixture();var expected=reply.Assignment.Clone();
        switch(damage)
        {
            case "assignment":reply.Assignment.TransitionId=Guid.NewGuid().ToString("D");break;
            case "runtime":reply.Basis.RuntimeInstanceId=Guid.NewGuid().ToString("D");break;
            case "profile":profile.Revision++;break;
            case "inventory":reply.Basis.MeasuredInventory.SizeBytes++;break;
            case "cut":reply.Basis.ProductionCutUtcTicks++;break;
            case "grant":reply.Basis.Attacker.ControlEpoch++;break;
            case "roster":start.Entities[0].Side=BattleFighterSide.Defender;break;
            case "native-in-basis":reply.Basis.InitialTick=1;break;
            case "duplicate":start.Entities[1].EntityId=start.Entities[0].EntityId;break;
        }
        Assert.Throws<InvalidDataException>(()=>BattlePreparationBuilder.Build(expected,profile,reply,start.InitialSnapshot,start.InitialTick,start.Entities));
    }
}
