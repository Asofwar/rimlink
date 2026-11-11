using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rimlink.BattlePrototype;

public enum BattleSide { Attacker = 1, Defender = 2 }
public enum BattleOutcome { Ongoing, AttackerWin, DefenderWin, Draw, TechnicalAbort }
public enum BattleEndReason { None, ZoneHeld, SideEliminated, MutualElimination, TickLimit, WallClockLimit, TransportLost, InvalidState }

public sealed record BattlePawn(string Id, BattleSide Side, int X, int Z, bool Alive = true,
    bool Downed = false, bool Mental = false, bool CanFight = true, float Health = 1)
{
    public bool CombatReady => Alive && !Downed && !Mental && CanFight;
}

public readonly record struct BattleZone(int MinX, int MinZ, int MaxX, int MaxZ)
{
    public bool Contains(BattlePawn pawn) => pawn.X >= MinX && pawn.X <= MaxX && pawn.Z >= MinZ && pawn.Z <= MaxZ;
}

public sealed record BattleRuleState(int Tick, int HeldTicks, BattleOutcome Outcome, BattleEndReason Reason);

public enum BattleRole { Arbiter = 1, SideA = 2, SideB = 3 }
public enum BattlePhase { Prepared, Started, Applied, TechnicalAbort, RecoveryRequired }
public enum BattleArtifactKind { InitialSnapshot = 1, FinalSnapshot = 2, Journal = 3 }
public enum BattleCommandKind { MoveTo = 1, Attack = 2, Draft = 3 }

public sealed record BattleIdentity(Guid CampaignId, Guid BattleId, Guid Boundary, long Fence,
    string RulesDigest, string TemplateDigest, string RosterDigest, long OwnerRevision);
public sealed record BattleArtifact(BattleArtifactKind Kind, long Bytes, string Sha256);
public sealed record BattleView(BattleIdentity Identity, long Sequence, int StartTick, BattleRuleState State,
    BattleZone Arena, BattleZone Zone, BattlePawn[] Pawns);
public sealed record BattleStart(BattleIdentity Identity, BattleArtifact Snapshot, BattleView View);
public sealed record BattleFinalResult(BattleIdentity Identity, BattleRuleState Decision,
    string InitialSnapshotSha256, BattleArtifact Snapshot, BattleArtifact Journal, BattlePawn[] Pawns);
public sealed record BattleReceipt(Guid BattleId, Guid Boundary, string ResultDigest, BattleOutcome Outcome,
    int TerminalTick, BattleSide Owner, long OwnerRevision, int OwnerChangedCount);
public sealed record BattleLedgerState(BattleIdentity Identity, BattlePhase Phase, BattleSide Owner,
    long OwnerRevision, int OwnerChangedCount, Guid Boundary, long Fence);
public sealed record BattleBinding(BattleRole Role, string PipeName, string Capability, BattleIdentity Identity,
    string StandRoot = "", string GameRoot = "", string SavedataRoot = "", string GameAssemblySha256 = "",
    string ClientAssemblySha256 = "", string CompanionAssemblySha256 = "");
public sealed record BattleCommand(long Sequence, BattleCommandKind Kind, string PawnId,
    int X = 0, int Z = 0, string? TargetPawnId = null, bool Drafted = true);
public sealed record QueuedBattleCommand(BattleSide Side, BattleCommand Command, string Digest);
public sealed record BattleCommandAck(BattleSide Side, long Sequence, string Digest, int Tick, bool Succeeded, string? Error, string JournalHash);
public sealed record BattleWireRequest(string Operation, string Capability, BattleIdentity Identity,
    BattleStart? Start = null, BattleCommand? Command = null, BattleView? View = null,
    BattleFinalResult? Result = null, BattleCommandAck? Ack = null);
public sealed record BattleWireReply(bool Success, string Code, BattleLedgerState? Ledger = null,
    BattleView? View = null, QueuedBattleCommand[]? Commands = null, BattleReceipt? Receipt = null, long NextSequence = 1,
    BattleCommandAck[]? Acks = null, bool AttackerOnline = false, bool DefenderOnline = false);
public sealed record BattlePreparation(BattleIdentity Identity, BattleZone Arena, BattleZone Zone, BattlePawn[] Pawns);

public static class BattleFixture
{
    public static string FaultPath(string root, string point)
    {
        if (point is not ("before-initial-file-complete" or "before-start-commit" or "after-start-commit" or "after-view-commit" or "after-final-durable" or "after-result-commit"))
            throw new InvalidDataException("unknown prototype fault point");
        string path = Path.Combine(root, "fault-" + point + ".armed"); CheckLocalPath(path); return path;
    }

    public static void CrashIfArmed(string root, string point)
    {
        string marker = FaultPath(root, point);
        if (!File.Exists(marker)) return;
        CheckLocalPath(marker + ".consumed"); File.Move(marker, marker + ".consumed", overwrite: false);
        Console.WriteLine(JsonSerializer.Serialize(new { fault = point })); Console.Out.Flush(); Environment.Exit(86);
    }

    public static void CheckLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("local absolute path required");
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("aliased stand path refused");
    }
    public static readonly BattleZone Arena = new(100, 100, 149, 149), Zone = new(122, 122, 127, 127);
    public static BattlePawn[] Roster() => Enumerable.Range(1, 4).Select(i => new BattlePawn("a" + i, BattleSide.Attacker, 110, 120 + i))
        .Concat(Enumerable.Range(1, 4).Select(i => new BattlePawn("b" + i, BattleSide.Defender, 139, 120 + i))).ToArray();
    public static string MatchRoot(BattleBinding binding) => Path.Combine(binding.StandRoot, "battles", binding.Identity.BattleId.ToString("N"));
    public static string ArtifactPath(BattleBinding binding, BattleArtifactKind kind) => Path.Combine(MatchRoot(binding), "artifacts",
        $"{binding.Identity.BattleId:N}-{kind switch { BattleArtifactKind.InitialSnapshot => "initial.rws", BattleArtifactKind.FinalSnapshot => "final.rws", BattleArtifactKind.Journal => "journal.jsonl", _ => throw new InvalidDataException("unknown artifact") }}");

    public static void CheckBinding(BattleBinding binding, string bindingFile, string processPath, string savedataPath,
        string gameHash, string clientHash, string companionHash, bool hasSession)
    {
        BattleContract.Validate(binding.Identity);
        string role = binding.Role switch { BattleRole.Arbiter => "arbiter", BattleRole.SideA => "side-a", BattleRole.SideB => "side-b", _ => throw new InvalidDataException("unknown role") };
        static bool Same(string a, string b) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (!Path.IsPathFullyQualified(binding.StandRoot) || binding.StandRoot.StartsWith("\\\\", StringComparison.Ordinal)
            || hasSession || !Same(binding.GameRoot, Path.Combine(binding.StandRoot, "game"))
            || !Same(binding.SavedataRoot, Path.Combine(binding.StandRoot, "savedata", role))
            || !Same(bindingFile, Path.Combine(MatchRoot(binding), role + ".json"))
            || !Same(processPath, Path.Combine(binding.GameRoot, "RimWorld.exe")) || !Same(savedataPath, binding.SavedataRoot)
            || !BattleContract.IsHash(binding.Capability) || gameHash != binding.GameAssemblySha256
            || clientHash != binding.ClientAssemblySha256 || companionHash != binding.CompanionAssemblySha256)
            throw new InvalidDataException("binding differs from the owned game process, build or session");
    }
}

public static class BattleContract
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();
    public static string RulesDigest(BattleZone arena, BattleZone zone) => Digest(new
    {
        Version = 1, Arena = arena, Zone = zone, BattleRules.CaptureTicks, BattleRules.MatchTicks,
        WallSeconds = (int)BattleRules.WallClockLimit.TotalSeconds, FightersPerSide = 4
    });

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void Validate(BattleIdentity identity)
    {
        if (identity is null || identity.CampaignId == Guid.Empty || identity.BattleId == Guid.Empty || identity.Boundary == Guid.Empty
            || identity.Fence <= 0 || identity.OwnerRevision <= 0 || !IsHash(identity.RulesDigest)
            || !IsHash(identity.TemplateDigest) || !IsHash(identity.RosterDigest))
            throw new InvalidDataException("invalid battle identity");
    }
}

public static class BattleFrames
{
    public const int RequestLimit = 16 * 1024;
    public const int ReplyLimit = 256 * 1024;

    public static async Task<BattleWireReply> Request(BattleBinding binding, BattleWireRequest request, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", binding.PipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        await Write(pipe, request, RequestLimit, deadline.Token);
        return await Read<BattleWireReply>(pipe, ReplyLimit, deadline.Token) ?? throw new EndOfStreamException("coordinator closed without a reply");
    }

    public static async Task<T?> Read<T>(Stream stream, int limit, CancellationToken ct = default) where T : class
    {
        byte[] header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), ct) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > limit) throw new InvalidDataException("frame size exceeds its limit");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        CheckNames(document.RootElement);
        return JsonSerializer.Deserialize<T>(body, BattleContract.Json) ?? throw new JsonException("null frame");
    }

    private static void CheckNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("duplicate field");
                CheckNames(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckNames(item);
    }

    public static async Task Write<T>(Stream stream, T value, int limit, CancellationToken ct = default)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, BattleContract.Json);
        if (body.Length == 0 || body.Length > limit) throw new InvalidDataException("frame size exceeds its limit");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(body, ct); await stream.FlushAsync(ct);
    }
}
