using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleManifestContractsTests
{
    private static ByteString Hash => ByteString.CopyFrom(SHA256.HashData("build"u8));
    private static BattleDocumentDescriptor Descriptor(BattleDocumentKind kind, long size = 1) => new() { Kind = kind, Sha256 = Hash, SizeBytes = size };
    internal static BattleRuntimeInventoryV1 Inventory()
    {
        var value = new BattleRuntimeInventoryV1 { Version = 1, GameBuildSha256 = Hash, RuntimeBuildSha256 = Hash,
            ClientBuildSha256 = Hash, ExecutorBuildSha256 = Hash, Config = Descriptor(BattleDocumentKind.Config), LoadedDefs = Descriptor(BattleDocumentKind.Defs) };
        value.Mods.Add(new BattleModInventoryEntry { LoadIndex = 0, PackageId = "rimlink", Version = "0.9.2", RootId = "rimlink" });
        value.Files.Add(new BattleFileInventoryEntry { Root = BattleInventoryRoot.Game, RootId = "game", RelativePath = "Assembly-CSharp.dll", SizeBytes = 1, Sha256 = Hash });
        value.Files.Add(new BattleFileInventoryEntry { Root = BattleInventoryRoot.Mod, RootId = "rimlink", RelativePath = "Assemblies/Rimlink.Client.dll", SizeBytes = 1, Sha256 = Hash });
        return value;
    }
    internal static BattleProfileV1 Profile()
    {
        var value = new BattleProfileV1 { Version = 1, ProfileId = Guid.NewGuid().ToString("D"), Revision = 1,
            Inventory = Descriptor(BattleDocumentKind.Inventory), ExecutorBuildSha256 = Hash, ViewerBuildSha256 = Hash,
            ViewerCompatibility = Descriptor(BattleDocumentKind.ViewerCompatibility), Template = Descriptor(BattleDocumentKind.Template),
            Rules = new() { CaptureTicks = 3000, MaximumTicks = 30000, WallSeconds = 1200, AiAfterSeconds = 30, TransportGraceSeconds = 60 },
            Arena = new() { Width = 50, Height = 50, ZoneMinX = 20, ZoneMinZ = 20, ZoneMaxX = 29, ZoneMaxZ = 29 },
            Limits = new() { CommandBytes = 16384, CommandsPerSecondPerSide = 20, PendingPerSide = 64, SnapshotChunkBytes = 262144,
                SnapshotBytes = 67108864, FullDeltaFrameBytes = 1048576 }, SnapshotEncodingVersion = 1, DeltaEncodingVersion = 1 };
        for (int i = 0; i < 8; i++) value.Fighters.Add(new BattleFighterSpec { FighterId = "fighter" + i,
            Side = i < 4 ? BattleFighterSide.Attacker : BattleFighterSide.Defender, PawnKindDef = "Colonist", RaceDef = "Human",
            AgeYears = 25, Gender = BattleFighterGender.Male, X = i < 4 ? 10 : 40, Z = 10 + i, WeaponDef = "Gun_BoltActionRifle", Shooting = 8, Melee = 8 });
        value.CommandAllowlist.Add(["Move", "Attack", "Draft"]);
        value.RosterDigest = ByteString.CopyFrom(BattleManifestContracts.RosterDigest(value.Fighters));
        return value;
    }
    internal static BattlePreparedManifestV1 Manifest()
    {
        var profile = Profile(); long start = new DateTime(2026,11,11,12,0,0,DateTimeKind.Utc).Ticks;
        var value = new BattlePreparedManifestV1 { Version = 1, WorldInstanceId = "world", RestoreBoundary = Guid.NewGuid().ToString("D"),
            CampaignId = Guid.NewGuid().ToString("D"), BattleId = Guid.NewGuid().ToString("D"), Attempt = 1, Fence = 1,
            OutpostId = Guid.NewGuid().ToString("D"), OwnerRevision = 1, AttackerAllianceId = 60, DefenderAllianceId = 50,
            ExecutorId = "arbiter-1", RuntimeInstanceId = Guid.NewGuid().ToString("D"), ExecutorLeaseId = Guid.NewGuid().ToString("D"),
            ExecutorLeaseFence = 1, StartNonce = Hash, ProfileId = profile.ProfileId, ProfileRevision = 1, ProfileDigest = Hash,
            ApprovalTransitionId = Guid.NewGuid().ToString("D"), MeasuredInventory = profile.Inventory.Clone(), RulesDigest = Hash,
            Template = profile.Template.Clone(), RosterDigest = profile.RosterDigest, InitialSnapshot = Descriptor(BattleDocumentKind.InitialSnapshot),
            InitialTick = 580, Arena = profile.Arena.Clone(), Attacker = new() { PlayerId = 2, AllianceId = 60, ControlGrantId = Guid.NewGuid().ToString("D"), SessionDigest = Hash, ControlEpoch = 1,
                MembershipJoinedUtcTicks = start - TimeSpan.TicksPerDay }, Defender = new() { DefenderAi = true },
            PreparationTransitionId = Guid.NewGuid().ToString("D"), ProductionCutUtcTicks = start, StockAtCut = 100,
            AcquisitionReservationId = Guid.NewGuid().ToString("D"), SlotId = Guid.NewGuid().ToString("D"),
            SlotStartUtcTicks = start, SlotEndUtcTicks = start + TimeSpan.TicksPerMinute * 30,
            ReadinessDeadlineUtcTicks = start + TimeSpan.TicksPerMinute * 5, ProposalSequence = 1 };
        foreach (var fighter in profile.Fighters) value.Entities.Add(new BattleEntitySide { FighterId = fighter.FighterId, EntityId = "Thing_" + fighter.FighterId, Side = fighter.Side });
        return value;
    }

    [Fact]
    public void Documents_roundtrip_exactly_and_digest_types_cannot_be_substituted()
    {
        var inventory = Inventory(); var profile = Profile(); var manifest = Manifest();
        Assert.Equal(inventory, BattleManifestContracts.ReadInventory(inventory.ToByteArray()));
        Assert.Equal(profile, BattleManifestContracts.ReadProfile(profile.ToByteArray()));
        Assert.Equal(manifest, BattleManifestContracts.ReadPrepared(manifest.ToByteArray()));
        byte[] bytes = inventory.ToByteArray();
        Assert.NotEqual(SHA256.HashData(bytes), BattleManifestContracts.Digest(BattleDocumentKind.Inventory, bytes));
        Assert.NotEqual(BattleManifestContracts.Digest(BattleDocumentKind.Inventory, bytes), BattleManifestContracts.Digest(BattleDocumentKind.Profile, bytes));
    }

    [Fact]
    public void The_fixed_baseliner_appearance_changes_the_roster_digest_and_preserves_historical_zero()
    {
        var value=Profile();byte[] historical=value.ToByteArray();var oldRoster=value.RosterDigest;
        Assert.Equal(historical,BattleManifestContracts.ReadProfile(historical).ToByteArray());
        foreach(var fighter in value.Fighters)fighter.BaselinerAppearanceVersion=1;
        Assert.Throws<InvalidDataException>(()=>BattleManifestContracts.ReadProfile(value.ToByteArray()));
        value.RosterDigest=ByteString.CopyFrom(BattleManifestContracts.RosterDigest(value.Fighters));
        Assert.NotEqual(oldRoster,value.RosterDigest);
        Assert.Equal(value,BattleManifestContracts.ReadProfile(value.ToByteArray()));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Unknown_or_mixed_baseliner_appearance_versions_are_refused_even_with_a_matching_digest(bool mixed)
    {
        var value=Profile();
        if(mixed)value.Fighters[0].BaselinerAppearanceVersion=1;
        else foreach(var fighter in value.Fighters)fighter.BaselinerAppearanceVersion=2;
        value.RosterDigest=ByteString.CopyFrom(BattleManifestContracts.RosterDigest(value.Fighters));
        Assert.Throws<InvalidDataException>(()=>BattleManifestContracts.ReadProfile(value.ToByteArray()));
    }

    [Theory]
    [InlineData("unknown")][InlineData("duplicate")][InlineData("oversize")][InlineData("truncated")]
    public void Unknown_duplicate_or_unbounded_wire_data_is_refused(string damage)
    {
        byte[] bytes = Inventory().ToByteArray();
        bytes = damage switch { "unknown" => [..bytes, 0xA0, 0x06, 0x01], "duplicate" => [0x08, 0x01, ..bytes],
            "oversize" => new byte[8*1024*1024+1], _ => bytes[..^1] };
        Assert.Throws<InvalidDataException>(() => BattleManifestContracts.ReadInventory(bytes));
    }

    [Theory]
    [InlineData("path")][InlineData("unc")][InlineData("case")][InlineData("order")][InlineData("root")][InlineData("build")][InlineData("mod")][InlineData("config")]
    public void Inventory_cannot_hide_ambiguous_paths_roots_or_missing_runtime_facts(string damage)
    {
        var value = Inventory();
        switch (damage) {
            case "path": value.Files[0].RelativePath = "a/../Assembly-CSharp.dll"; break;
            case "unc": value.Files[0].RelativePath = "//server/file"; break;
            case "case": value.Files.Insert(1, value.Files[0].Clone()); value.Files[1].RelativePath = "assembly-csharp.dll"; break;
            case "order": var first = value.Files[0]; value.Files[0] = value.Files[1]; value.Files[1] = first; break;
            case "root": value.Files[1].RootId = "missing"; break;
            case "build": value.RuntimeBuildSha256 = ByteString.Empty; break;
            case "mod": value.Mods[0].LoadIndex = 1; break;
            case "config": value.Config.Kind = BattleDocumentKind.InitialSnapshot; break;
        }
        Assert.Throws<InvalidDataException>(() => BattleManifestContracts.ReadInventory(value.ToByteArray()));
    }

    [Theory]
    [InlineData("roster")][InlineData("side")][InlineData("geometry")][InlineData("rule")][InlineData("limits")][InlineData("command")][InlineData("encoding")][InlineData("executable-exclusion")]
    public void Profile_has_a_fixed_four_by_four_roster_and_the_approved_rules_and_admission_limits(string damage)
    {
        var value = Profile();
        switch (damage) {
            case "roster": value.Fighters.RemoveAt(7); break;
            case "side": value.Fighters[0].Side = BattleFighterSide.Defender; break;
            case "geometry": value.Fighters[0].X = value.Arena.Width; break;
            case "rule": value.Rules.CaptureTicks = 1; break;
            case "limits": value.Limits.SnapshotBytes++; break;
            case "command": value.CommandAllowlist.Add("TakeJob"); break;
            case "encoding": value.SnapshotEncodingVersion = 0; break;
            case "executable-exclusion": value.ExcludedRuntimePaths.Add(new BattleExcludedRuntimePath { Root = BattleInventoryRoot.Mod, RootId = "rimlink", RelativePath = "Cache/code.dll" }); break;
        }
        Assert.Throws<InvalidDataException>(() => BattleManifestContracts.ReadProfile(value.ToByteArray()));
    }

    [Theory]
    [InlineData("ai-attacker")][InlineData("ai-secret")][InlineData("entity")][InlineData("side")][InlineData("slot")][InlineData("deadline")][InlineData("tick")][InlineData("proposal")][InlineData("nonce")][InlineData("same-alliance")]
    public void Prepared_manifest_is_finite_and_cannot_change_role_or_timing_meaning(string damage)
    {
        var value = Manifest();
        switch (damage) {
            case "ai-attacker": value.Attacker = new() { DefenderAi = true }; break;
            case "ai-secret": value.Defender.PlayerId = 1; break;
            case "entity": value.Entities[7].EntityId = value.Entities[0].EntityId; break;
            case "side": value.Entities[0].Side = BattleFighterSide.Defender; break;
            case "slot": value.SlotEndUtcTicks++; break;
            case "deadline": value.ReadinessDeadlineUtcTicks++; break;
            case "tick": value.InitialTick = int.MaxValue; break;
            case "proposal": value.ProposalSequence = 9; break;
            case "nonce": value.StartNonce = ByteString.CopyFrom(new byte[31]); break;
            case "same-alliance": value.DefenderAllianceId = value.AttackerAllianceId; break;
        }
        Assert.Throws<InvalidDataException>(() => BattleManifestContracts.ReadPrepared(value.ToByteArray()));
    }
}
