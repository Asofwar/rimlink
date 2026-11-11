using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static partial class BattleManifestContracts
{
    public const uint StandardBaselinerAppearanceVersion = 1;
    public const int InventoryBytes = 8 * 1024 * 1024;
    public const int ProfileBytes = 16 * 1024;
    public const int PreparedBytes = 8 * 1024;
    public const int FinalBytes = 8 * 1024;
    private const long SnapshotBytes = 64L * 1024 * 1024;

    public static BattleRuntimeInventoryV1 ReadInventory(ReadOnlySpan<byte> bytes)
    {
        var value = Read(bytes, InventoryBytes, BattleRuntimeInventoryV1.Parser);
        if (value.Version != 1 || value.Mods.Count is < 1 or > 256 || value.Files.Count is < 1 or > 16384
            || !Hash(value.GameBuildSha256) || !Hash(value.RuntimeBuildSha256) || !Hash(value.ClientBuildSha256) || !Hash(value.ExecutorBuildSha256)) throw Invalid();
        RequireDescriptor(value.Config, BattleDocumentKind.Config); RequireDescriptor(value.LoadedDefs, BattleDocumentKind.Defs);
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < value.Mods.Count; i++)
        {
            var mod = value.Mods[i];
            if (mod.LoadIndex != i || !Text(mod.PackageId,128) || !Text(mod.Version,128) || !Identifier(mod.RootId)
                || !mods.Add(mod.PackageId) || !roots.Add(mod.RootId)) throw Invalid();
        }
        RequireOrdered(value.ActiveDlc, x => x, x => Identifier(x), StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        long total = 0;
        foreach (var file in value.Files)
        {
            if (!Root(file.Root) || !Identifier(file.RootId) || !Path(file.RelativePath) || file.SizeBytes < 0 || !Hash(file.Sha256)
                || file.Root == BattleInventoryRoot.Mod && !value.Mods.Any(m => m.RootId == file.RootId)) throw Invalid();
            string key = FileKey(file.Root,file.RootId,file.RelativePath);
            if (previous is not null && StringComparer.Ordinal.Compare(previous,key) >= 0 || !aliases.Add(key)) throw Invalid();
            previous = key;
            try { total = checked(total + file.SizeBytes); } catch (OverflowException) { throw Invalid(); }
        }
        return value;
    }

    public static BattleProfileV1 ReadProfile(ReadOnlySpan<byte> bytes)
    {
        var value = Read(bytes, ProfileBytes, BattleProfileV1.Parser);
        if (value.Version != 1 || !Uuid(value.ProfileId) || value.Revision <= 0 || !Hash(value.ExecutorBuildSha256) || !Hash(value.ViewerBuildSha256)
            || value.SnapshotEncodingVersion != 1 || value.DeltaEncodingVersion != 1) throw Invalid();
        RequireDescriptor(value.Inventory,BattleDocumentKind.Inventory); RequireDescriptor(value.Template,BattleDocumentKind.Template);
        RequireDescriptor(value.ViewerCompatibility,BattleDocumentKind.ViewerCompatibility);
        if (value.Rules is not { CaptureTicks:3000, MaximumTicks:30000, WallSeconds:1200, AiAfterSeconds:30, TransportGraceSeconds:60 }
            || value.Limits is not { CommandBytes:16384, CommandsPerSecondPerSide:20, PendingPerSide:64, SnapshotChunkBytes:262144,
                SnapshotBytes:67108864, FullDeltaFrameBytes:1048576 }
            || !value.CommandAllowlist.SequenceEqual(new[] { "Move","Attack","Draft" })) throw Invalid();
        Arena(value.Arena);
        if (value.Fighters.Count != 8 || value.Fighters.Count(f => f.Side == BattleFighterSide.Attacker) != 4
            || value.Fighters.Count(f => f.Side == BattleFighterSide.Defender) != 4
            || value.Fighters.Any(f=>f.BaselinerAppearanceVersion>StandardBaselinerAppearanceVersion
                || f.BaselinerAppearanceVersion!=value.Fighters[0].BaselinerAppearanceVersion)) throw Invalid();
        RequireOrdered(value.Fighters,f => f.FighterId,f => Identifier(f.FighterId) && Text(f.FighterId,64),StringComparer.Ordinal);
        var positions = new HashSet<(int,int)>();
        foreach (var fighter in value.Fighters)
        {
            if (!Def(fighter.PawnKindDef) || !Def(fighter.RaceDef) || !Def(fighter.WeaponDef) || fighter.AgeYears is < 18 or > 60
                || fighter.Gender is not (BattleFighterGender.Male or BattleFighterGender.Female) || fighter.X < 0 || fighter.X >= value.Arena.Width
                || fighter.Z < 0 || fighter.Z >= value.Arena.Height || !positions.Add((fighter.X,fighter.Z))
                || fighter.Shooting is < 0 or > 20 || fighter.Melee is < 0 or > 20 || fighter.ApparelDefs.Count > 16) throw Invalid();
            RequireOrdered(fighter.ApparelDefs,x => x,Def,StringComparer.Ordinal);
        }
        if (!Hash(value.RosterDigest) || !RosterDigest(value.Fighters).AsSpan().SequenceEqual(value.RosterDigest.Span)) throw Invalid();
        if (value.ExcludedRuntimePaths.Count > 128) throw Invalid();
        RequireOrdered(value.ExcludedRuntimePaths,p => FileKey(p.Root,p.RootId,p.RelativePath),p => Root(p.Root) && Identifier(p.RootId)
            && Path(p.RelativePath) && SafeExcluded(p.RelativePath),StringComparer.OrdinalIgnoreCase);
        return value;
    }

    public static BattleViewerCompatibilityV1 ReadViewerCompatibility(ReadOnlySpan<byte> bytes)
    {
        var value=Read(bytes,ProfileBytes,BattleViewerCompatibilityV1.Parser);
        if(value.Version!=1 || !Hash(value.ViewerBuildSha256) || value.SnapshotEncodingVersion!=1 || value.DeltaEncodingVersion!=1)throw Invalid();
        RequireDescriptor(value.Inventory,BattleDocumentKind.Inventory);return value;
    }

    public static BattlePreparedManifestV1 ReadPrepared(ReadOnlySpan<byte> bytes)
    {
        var value = Read(bytes,PreparedBytes,BattlePreparedManifestV1.Parser);
        if (value.Version != 1 || !Text(value.WorldInstanceId,256) || !Uuid(value.RestoreBoundary) || !Uuid(value.CampaignId)
            || !Uuid(value.BattleId) || value.Attempt <= 0 || value.Fence <= 0 || !Uuid(value.OutpostId) || value.OwnerRevision <= 0
            || value.AttackerAllianceId <= 0 || value.DefenderAllianceId <= 0 || value.AttackerAllianceId == value.DefenderAllianceId
            || !Identifier(value.ExecutorId) || !Uuid(value.RuntimeInstanceId) || !Uuid(value.ExecutorLeaseId) || value.ExecutorLeaseFence <= 0
            || !Hash(value.StartNonce) || !Uuid(value.ProfileId) || value.ProfileRevision <= 0 || !Hash(value.ProfileDigest)
            || !Uuid(value.ApprovalTransitionId) || !Hash(value.RulesDigest) || !Hash(value.RosterDigest)
            || value.InitialTick < 0 || value.InitialTick > int.MaxValue - 30000 || !Uuid(value.PreparationTransitionId)
            || value.StockAtCut is < 0 or > 1000 || value.RemainderAtCut is < 0 or >= TimeSpan.TicksPerHour
            || !Uuid(value.AcquisitionReservationId) || !Uuid(value.SlotId) || value.ProposalSequence is < 1 or > 8) throw Invalid();
        RequireDescriptor(value.MeasuredInventory,BattleDocumentKind.Inventory); RequireDescriptor(value.Template,BattleDocumentKind.Template);
        RequireDescriptor(value.InitialSnapshot,BattleDocumentKind.InitialSnapshot); Arena(value.Arena);
        if (!UtcTicks(value.SlotStartUtcTicks) || !UtcTicks(value.SlotEndUtcTicks) || !UtcTicks(value.ReadinessDeadlineUtcTicks)
            || !UtcTicks(value.ProductionCutUtcTicks) || value.SlotEndUtcTicks - value.SlotStartUtcTicks != 30 * TimeSpan.TicksPerMinute
            || value.ReadinessDeadlineUtcTicks - value.SlotStartUtcTicks != 5 * TimeSpan.TicksPerMinute
            || value.ProductionCutUtcTicks < value.SlotStartUtcTicks || value.ProductionCutUtcTicks >= value.ReadinessDeadlineUtcTicks) throw Invalid();
        Commander(value.Attacker,false); Commander(value.Defender,true);
        if (value.Attacker.AllianceId != value.AttackerAllianceId || !value.Defender.DefenderAi && (value.Defender.AllianceId != value.DefenderAllianceId || value.Attacker.PlayerId == value.Defender.PlayerId) || value.Entities.Count != 8
            || value.Entities.Count(e => e.Side == BattleFighterSide.Attacker) != 4 || value.Entities.Count(e => e.Side == BattleFighterSide.Defender) != 4) throw Invalid();
        RequireOrdered(value.Entities,e => e.FighterId,e => Identifier(e.FighterId) && Text(e.FighterId,64) && Identifier(e.EntityId) && Text(e.EntityId,64),StringComparer.Ordinal);
        if (value.Entities.Select(e => e.EntityId).Distinct(StringComparer.Ordinal).Count() != 8) throw Invalid();
        return value;
    }

    public static byte[] Digest(BattleDocumentKind kind, ReadOnlySpan<byte> canonicalBytes)
    {
        string domain = kind switch { BattleDocumentKind.Inventory => "Rimlink.BattleRuntimeInventory/v1\0", BattleDocumentKind.Profile => "Rimlink.BattleProfile/v1\0",
            BattleDocumentKind.PreparedManifest => "Rimlink.BattlePreparedManifest/v1\0", BattleDocumentKind.FinalManifest => "Rimlink.BattleFinalManifest/v1\0", BattleDocumentKind.CommittedFinal => "Rimlink.BattleCommittedFinal/v1\0", _ => throw Invalid() };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(domain)); hash.AppendData(canonicalBytes); return hash.GetHashAndReset();
    }

    public static byte[] RosterDigest(IReadOnlyList<BattleFighterSpec> fighters)
    {
        if (fighters.Count != 8) throw Invalid();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Rimlink.BattleRoster/v1\0"u8); Span<byte> length = stackalloc byte[4];
        foreach (var fighter in fighters)
        {
            if (fighter is null || fighter.CalculateSize() > 1024) throw Invalid();
            byte[] bytes = fighter.ToByteArray(); BinaryPrimitives.WriteInt32LittleEndian(length,bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return hash.GetHashAndReset();
    }

    public static void RequireDescriptor(BattleDocumentDescriptor? value, BattleDocumentKind kind)
    {
        long maximum = kind switch { BattleDocumentKind.Inventory or BattleDocumentKind.Config or BattleDocumentKind.Defs or BattleDocumentKind.Qualification => InventoryBytes,
            BattleDocumentKind.Profile or BattleDocumentKind.ViewerCompatibility => ProfileBytes, BattleDocumentKind.PreparedManifest => PreparedBytes, BattleDocumentKind.FinalManifest => FinalBytes, BattleDocumentKind.CommittedFinal or BattleDocumentKind.TransportSeal => 16384,
            BattleDocumentKind.TerminalView => 1024 * 1024,
            BattleDocumentKind.Template or BattleDocumentKind.InitialSnapshot or BattleDocumentKind.FinalSnapshot or BattleDocumentKind.SimulationJournal or BattleDocumentKind.TransportJournal => SnapshotBytes, _ => throw Invalid() };
        if (value is null || value.Kind != kind || !Hash(value.Sha256) || value.SizeBytes < 1 || value.SizeBytes > maximum) throw Invalid();
    }

    private static T Read<T>(ReadOnlySpan<byte> bytes, int maximum, MessageParser<T> parser) where T : IMessage<T>
    {
        if (bytes.Length < 1 || bytes.Length > maximum) throw Invalid();
        try
        {
            var value = parser.WithDiscardUnknownFields(true).ParseFrom(bytes.ToArray());
            if (!value.ToByteArray().AsSpan().SequenceEqual(bytes)) throw Invalid();
            return value;
        }
        catch (InvalidProtocolBufferException error) { throw new InvalidDataException("battle-document-invalid",error); }
    }
    private static void Arena(BattleArenaSpec? arena)
    {
        if (arena is null || arena.Width is < 1 or > 1000 || arena.Height is < 1 or > 1000 || arena.ZoneMinX < 0 || arena.ZoneMinZ < 0
            || arena.ZoneMaxX < arena.ZoneMinX || arena.ZoneMaxZ < arena.ZoneMinZ || arena.ZoneMaxX >= arena.Width || arena.ZoneMaxZ >= arena.Height) throw Invalid();
    }
    private static void Commander(BattleCommanderBinding? value, bool allowAi)
    {
        if (value is null) throw Invalid();
        if (value.DefenderAi)
        {
            if (!allowAi || value.ControlGrantId.Length != 0 || value.AllianceId != 0 || value.PlayerId != 0 || value.SessionDigest.Length != 0 || value.ControlEpoch != 0 || value.MembershipJoinedUtcTicks != 0) throw Invalid();
        }
        else if (!Uuid(value.ControlGrantId) || value.AllianceId <= 0 || value.PlayerId <= 0 || !Hash(value.SessionDigest) || value.ControlEpoch <= 0 || !UtcTicks(value.MembershipJoinedUtcTicks)) throw Invalid();
    }
    private static void RequireOrdered<T>(IEnumerable<T> values, Func<T,string> key, Func<T,bool> valid, StringComparer comparer)
    {
        string? previous = null;
        foreach (var value in values)
        {
            string current = key(value);
            if (!valid(value) || previous is not null && comparer.Compare(previous,current) >= 0) throw Invalid();
            previous = current;
        }
    }
    private static string FileKey(BattleInventoryRoot root, string id, string path) => ((int)root).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + id + "/" + path;
    private static bool Hash(ByteString value) => value.Length == 32;
    private static bool Uuid(string value) => Guid.TryParseExact(value,"D",out var id) && id != Guid.Empty && id.ToString("D") == value;
    private static bool UtcTicks(long value) => value > 0 && value <= DateTime.MaxValue.Ticks;
    private static bool Root(BattleInventoryRoot value) => value is BattleInventoryRoot.Game or BattleInventoryRoot.Runtime or BattleInventoryRoot.Mod;
    private static bool Text(string value,int maximum) => !string.IsNullOrWhiteSpace(value) && Encoding.UTF8.GetByteCount(value) <= maximum && !value.Any(char.IsControl) && value.IsNormalized(NormalizationForm.FormC);
    private static bool Identifier(string value) => Text(value,128) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static bool Def(string value) => Identifier(value);
    private static bool Path(string value)
    {
        if (!Text(value,512) || value.Contains('\\') || value.Any(c => c is ':' or '*' or '?' or '"' or '<' or '>' or '|')) return false;
        foreach (string part in value.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.TrimEnd('.',' ') != part) return false;
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9') return false;
        }
        return true;
    }
    private static bool SafeExcluded(string path)
    {
        string first = path.Split('/')[0];
        if (first is not ("Logs" or "Cache" or "Savedata")) return false;
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return extension is ".log" or ".rws" or ".tmp";
    }
    private static InvalidDataException Invalid() => new("battle-document-invalid");
}
