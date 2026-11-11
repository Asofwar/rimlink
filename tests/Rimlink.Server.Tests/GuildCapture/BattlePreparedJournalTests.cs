using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattlePreparedJournalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),"guild-proposal-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Only_exact_durable_proposals_are_ready_enabled_and_superseded_bytes_are_preserved()
    {
        var journal = new BattlePreparedJournal(new BattleArtifactStore(root)); var manifest = Manifest();
        var first = journal.ProposeHeld(manifest.ToByteArray());
        Assert.Equal(first.Manifest, journal.ProposeHeld(manifest.ToByteArray()).Manifest);
        Assert.Equal(first.ManifestDigest,journal.LatestHeld(manifest.BattleId,manifest.Fence)!.ManifestDigest);
        var second = manifest.Clone(); second.ProposalSequence = 2; second.Attacker.ControlEpoch++;
        journal.ProposeHeld(second.ToByteArray());
        Assert.Equal(2,new BattlePreparedJournal(new BattleArtifactStore(root)).LatestHeld(manifest.BattleId,manifest.Fence)!.Sequence);
        Assert.Equal(first.Manifest,journal.ReadHeld(manifest.BattleId,manifest.Fence,1).Manifest);
        var conflicting = manifest.Clone(); conflicting.InitialTick++;
        Assert.Throws<InvalidDataException>(() => journal.ProposeHeld(conflicting.ToByteArray()));
    }
    [Fact]
    public void Gaps_partial_newer_records_and_unknown_sequences_do_not_fall_back_to_an_old_ready()
    {
        var store = new BattleArtifactStore(root); var journal = new BattlePreparedJournal(store); var manifest = Manifest();
        manifest.ProposalSequence = 2;
        Assert.Throws<InvalidDataException>(() => journal.ProposeHeld(manifest.ToByteArray()));
        manifest.ProposalSequence = 1; journal.ProposeHeld(manifest.ToByteArray());
        manifest.ProposalSequence = 2;
        var second = journal.ProposeHeld(manifest.ToByteArray());
        string complete = Path.Combine(root,second.Artifact.RelativePath.Replace('/',Path.DirectorySeparatorChar));
        File.Move(complete,complete.Replace(".artifact",".part"));
        Assert.Throws<InvalidDataException>(() => journal.LatestHeld(manifest.BattleId,manifest.Fence));
        Assert.Equal(2,journal.ProposeHeld(manifest.ToByteArray()).Sequence);
        manifest.ProposalSequence = 9;
        Assert.Throws<InvalidDataException>(() => journal.ProposeHeld(manifest.ToByteArray()));
    }
    [Fact]
    public void New_proposal_cannot_rotate_the_lease_profile_boundary_or_initial_snapshot()
    {
        var journal = new BattlePreparedJournal(new BattleArtifactStore(root)); var manifest = Manifest(); journal.ProposeHeld(manifest.ToByteArray());
        foreach (string change in new[] { "lease","profile","boundary","initial","nonce","runtime" })
        {
            var second = manifest.Clone(); second.ProposalSequence = 2;
            switch (change) {
                case "lease": second.ExecutorLeaseId = Guid.NewGuid().ToString("D"); break;
                case "profile": second.ProfileDigest = ByteString.CopyFrom(new byte[32]); break;
                case "boundary": second.RestoreBoundary = Guid.NewGuid().ToString("D"); break;
                case "initial": second.InitialSnapshot.SizeBytes++; break;
                case "nonce": second.StartNonce = ByteString.CopyFrom(new byte[32]); break;
                case "runtime": second.RuntimeInstanceId = Guid.NewGuid().ToString("D"); break;
            }
            Assert.Throws<InvalidDataException>(() => journal.ProposeHeld(second.ToByteArray()));
        }
    }
    internal static BattlePreparedManifestV1 Manifest()
    {
        ByteString hash = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData("fixture"u8));
        BattleDocumentDescriptor Ref(BattleDocumentKind kind) => new() { Kind = kind,Sha256 = hash,SizeBytes = 10 };
        long start = new DateTime(2026,11,11,12,0,0,DateTimeKind.Utc).Ticks;
        var value = new BattlePreparedManifestV1 { Version = 1,WorldInstanceId = "world",RestoreBoundary = Guid.NewGuid().ToString("D"),
            CampaignId = Guid.NewGuid().ToString("D"),BattleId = Guid.NewGuid().ToString("D"),Attempt = 1,Fence = 1,
            OutpostId = Guid.NewGuid().ToString("D"),OwnerRevision = 1,AttackerAllianceId = 60,DefenderAllianceId = 50,
            ExecutorId = "arbiter-1",RuntimeInstanceId = Guid.NewGuid().ToString("D"),ExecutorLeaseId = Guid.NewGuid().ToString("D"),ExecutorLeaseFence = 1,
            StartNonce = hash,ProfileId = Guid.NewGuid().ToString("D"),ProfileRevision = 1,ProfileDigest = hash,ApprovalTransitionId = Guid.NewGuid().ToString("D"),
            MeasuredInventory = Ref(BattleDocumentKind.Inventory),RulesDigest = hash,Template = Ref(BattleDocumentKind.Template),RosterDigest = hash,
            InitialSnapshot = Ref(BattleDocumentKind.InitialSnapshot),InitialTick = 580,
            Arena = new() { Width = 50,Height = 50,ZoneMinX = 20,ZoneMinZ = 20,ZoneMaxX = 29,ZoneMaxZ = 29 },
            Attacker = new() { PlayerId = 2,AllianceId = 60,ControlGrantId = Guid.NewGuid().ToString("D"),SessionDigest = hash,ControlEpoch = 1,MembershipJoinedUtcTicks = start - TimeSpan.TicksPerDay },Defender = new() { DefenderAi = true },
            PreparationTransitionId = Guid.NewGuid().ToString("D"),ProductionCutUtcTicks = start,StockAtCut = 100,
            AcquisitionReservationId = Guid.NewGuid().ToString("D"),SlotId = Guid.NewGuid().ToString("D"),SlotStartUtcTicks = start,
            SlotEndUtcTicks = start + 30 * TimeSpan.TicksPerMinute,ReadinessDeadlineUtcTicks = start + 5 * TimeSpan.TicksPerMinute,ProposalSequence = 1 };
        for (int i = 0;i < 8;i++) value.Entities.Add(new BattleEntitySide { FighterId = "fighter" + i,EntityId = "Thing_fighter" + i,
            Side = i < 4 ? BattleFighterSide.Attacker : BattleFighterSide.Defender });
        return value;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root,true); }
}
