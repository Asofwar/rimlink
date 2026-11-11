using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public static class BattlePreparationBuilder
{
    public static BattlePreparedManifestV1 Build(BattleRuntimeAssignmentV1 expected,BattleProfileV1 measuredProfile,BattlePreparationBasisReply reply,
        BattleDocumentDescriptor initial,int initialTick,IReadOnlyList<BattleEntitySide> entities)
    {
        expected=BattleRuntimeAssignmentContracts.Read(expected.ToByteArray());
        measuredProfile=BattleManifestContracts.ReadProfile(measuredProfile.ToByteArray());
        if(reply is null || reply.CalculateSize()>32768 || !expected.Equals(reply.Assignment) || reply.Basis is not{} basis
            || basis.InitialSnapshot is not null || basis.InitialTick!=0 || basis.Entities.Count!=0 || entities.Count!=8
            || basis.MeasuredInventory is null || basis.Template is null || basis.Arena is null)throw Invalid();
        basis=basis.Clone();var scope=expected.Scope;
        if(basis.WorldInstanceId!=scope.WorldInstanceId || basis.RestoreBoundary!=scope.RestoreBoundary || basis.BattleId!=scope.BattleId
            || basis.Attempt!=scope.Attempt || basis.Fence!=scope.Fence || basis.CampaignId!=expected.CampaignId || basis.ExecutorId!=expected.ExecutorId
            || basis.RuntimeInstanceId!=expected.RuntimeInstanceId || basis.ExecutorLeaseId!=expected.OriginalLeaseId || basis.ExecutorLeaseFence!=expected.OriginalLeaseFence
            || !basis.StartNonce.Equals(expected.StartNonce) || basis.ApprovalTransitionId!=expected.ApprovalTransitionId || basis.PreparationTransitionId!=expected.TransitionId
            || basis.ProductionCutUtcTicks!=expected.AssignedAtUtcTicks || !basis.ProfileDigest.Equals(expected.ProfileDigest)
            || !expected.ProfileDigest.Span.SequenceEqual(BattleManifestContracts.Digest(BattleDocumentKind.Profile,measuredProfile.ToByteArray()))
            || basis.ProfileId!=measuredProfile.ProfileId || basis.ProfileRevision!=measuredProfile.Revision || !basis.MeasuredInventory.Equals(measuredProfile.Inventory)
            || !expected.InventorySha256.Equals(measuredProfile.Inventory.Sha256) || !expected.ExecutorBuildSha256.Equals(measuredProfile.ExecutorBuildSha256)
            || !basis.Template.Equals(measuredProfile.Template) || !basis.RosterDigest.Equals(measuredProfile.RosterDigest) || !basis.Arena.Equals(measuredProfile.Arena)
            || basis.Attacker is null || basis.Attacker.ControlEpoch!=basis.ProposalSequence || basis.Defender is null
            || !basis.Defender.DefenderAi && basis.Defender.ControlEpoch!=basis.ProposalSequence)throw Invalid();
        for(int i=0;i<8;i++)
            if(entities[i] is null || entities[i].FighterId!=measuredProfile.Fighters[i].FighterId || entities[i].Side!=measuredProfile.Fighters[i].Side)throw Invalid();
        basis.InitialSnapshot=initial.Clone();basis.InitialTick=initialTick;basis.Entities.Add(entities.Select(e=>e.Clone()));
        return BattleManifestContracts.ReadPrepared(basis.ToByteArray());
    }
    private static InvalidDataException Invalid()=>new("battle-preparation-basis-or-native-measurement-invalid");
}
