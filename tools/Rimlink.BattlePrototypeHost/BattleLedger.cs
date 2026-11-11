using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rimlink.BattlePrototype;

public sealed class BattleLedger : IDisposable
{
    private readonly string root;
    private readonly BattleIdentity identity;
    private readonly SqliteConnection db;
    private readonly object sync = new();
    private SqliteTransaction? transaction;
    internal string Root => root;
    public BattleLedgerState State { get { lock (sync) return ReadState(); } }
    public BattleLedger(string root, BattleIdentity identity)
    {
        BattleContract.Validate(identity);
        this.root = OwnedRoot(root);
        this.identity = identity;
        Directory.CreateDirectory(Path.Combine(this.root, "artifacts"));
        CheckLinks(Path.Combine(this.root, "battle.db"));
        db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(this.root, "battle.db"), Pooling = false }.ToString());
        try
        {
            db.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
            Execute("""
                CREATE TABLE IF NOT EXISTS state (
                    id INTEGER PRIMARY KEY CHECK(id=1), schema_version INTEGER NOT NULL CHECK(schema_version=1),
                    identity_json TEXT NOT NULL, phase INTEGER NOT NULL, owner INTEGER NOT NULL,
                    owner_revision INTEGER NOT NULL, owner_changes INTEGER NOT NULL, boundary TEXT NOT NULL, fence INTEGER NOT NULL,
                    start_json TEXT NULL, result_json TEXT NULL, receipt_json TEXT NULL, view_json TEXT NULL);
                CREATE TABLE IF NOT EXISTS commands (
                    side INTEGER NOT NULL, sequence INTEGER NOT NULL, digest TEXT NOT NULL,
                    command_json TEXT NOT NULL, ack_json TEXT NULL, PRIMARY KEY(side,sequence));
                """);
            Execute("INSERT OR IGNORE INTO state(id,schema_version,identity_json,phase,owner,owner_revision,owner_changes,boundary,fence) VALUES(1,1,$i,0,2,$r,0,$b,$f)",
                ("$i", Serialize(identity)), ("$r", identity.OwnerRevision), ("$b", identity.Boundary.ToString()), ("$f", identity.Fence));
            if (ReadState().Identity != identity) throw new InvalidDataException("fixture belongs to another battle");
            try
            {
                if (Text("start_json") is { } startJson) VerifyArtifact(Deserialize<BattleStart>(startJson).Snapshot);
                if (Text("result_json") is { } resultJson)
                {
                    var result = Deserialize<BattleFinalResult>(resultJson);
                    VerifyArtifact(result.Snapshot); VerifyArtifact(result.Journal);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { Execute("UPDATE state SET phase=$p WHERE id=1", ("$p", (int)BattlePhase.RecoveryRequired)); }
        }
        catch { db.Dispose(); throw; }
    }

    public string ArtifactPath(BattleArtifactKind kind)
    {
        string suffix = kind switch
        {
            BattleArtifactKind.InitialSnapshot => "initial.rws", BattleArtifactKind.FinalSnapshot => "final.rws",
            BattleArtifactKind.Journal => "journal.jsonl", _ => throw new InvalidDataException("unknown artifact kind")
        };
        return Path.Combine(root, "artifacts", $"{identity.BattleId:N}-{suffix}");
    }

    public void Start(BattleRole role, BattleStart start)
    {
        RequireArbiter(role);
        lock (sync)
        {
            using var tx = db.BeginTransaction(); transaction = tx;
            try
            {
                EnsureIdentity(start.Identity);
                string json = Serialize(start);
                if (Text("start_json") is { } prior)
                {
                    if (prior != json || ReadState().Phase != BattlePhase.Started) throw new InvalidDataException("start conflict or terminal battle");
                    VerifyArtifact(start.Snapshot); tx.Commit(); return;
                }
                if (ReadState().Phase != BattlePhase.Prepared) throw new InvalidDataException("battle is not prepared");
                var view = start.View;
                ValidateRoster(view.Pawns, view.Arena);
                if (start.Snapshot.Kind != BattleArtifactKind.InitialSnapshot || view.Identity != identity || view.Sequence != 0
                    || view.State.Tick != view.StartTick || view.State.HeldTicks != 0 || view.State.Outcome != BattleOutcome.Ongoing
                    || view.State.Reason != BattleEndReason.None || view.Pawns.Any(p => !p.CombatReady || p.Health != 1)
                    || !Contains(view.Arena, view.Zone.MinX, view.Zone.MinZ) || !Contains(view.Arena, view.Zone.MaxX, view.Zone.MaxZ)
                    || BattleContract.RulesDigest(view.Arena, view.Zone) != identity.RulesDigest || BattleContract.Digest(view.Pawns) != identity.RosterDigest)
                    throw new InvalidDataException("initial battle state differs from its prepared contract");
                _ = new BattleRules(view.Zone, view.StartTick);
                VerifyArtifact(start.Snapshot);
                Execute("UPDATE state SET phase=$p,start_json=$s,view_json=$v WHERE id=1", ("$p", (int)BattlePhase.Started), ("$s", json), ("$v", Serialize(view)));
                tx.Commit();
            }
            finally { transaction = null; }
        }
    }

    public BattleReceipt Apply(BattleRole role, BattleFinalResult result)
    {
        RequireArbiter(role);
        lock (sync)
        {
            using var tx = db.BeginTransaction(); transaction = tx;
            try
            {
                EnsureIdentity(result.Identity);
                var state = ReadState();
                string digest = BattleContract.Digest(result);
                var prior = Text("receipt_json") is { } receiptJson ? Deserialize<BattleReceipt>(receiptJson) : null;
                if (prior is not null && prior.ResultDigest != digest) throw new InvalidDataException("conflicting final result");
                if (state.Phase != BattlePhase.Started && !(state.Phase is BattlePhase.Applied or BattlePhase.TechnicalAbort && prior is not null))
                    throw new InvalidDataException("battle cannot accept a result");
                var start = Deserialize<BattleStart>(Text("start_json") ?? throw new InvalidDataException("missing committed start"));
                ValidateFinal(start, result);
                try { VerifyArtifact(start.Snapshot); VerifyArtifact(result.Snapshot); VerifyArtifact(result.Journal); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Execute("UPDATE state SET phase=$p WHERE id=1", ("$p", (int)BattlePhase.RecoveryRequired)); tx.Commit();
                    throw new InvalidDataException("committed battle evidence is missing or corrupt; recovery required", ex);
                }
                var view = Deserialize<BattleView>(Text("view_json") ?? throw new InvalidDataException("missing battle view"));
                if (view.State != result.Decision || !view.Pawns.SequenceEqual(result.Pawns))
                    view = view with { Sequence = checked(view.Sequence + 1), State = result.Decision, Pawns = result.Pawns };
                string terminalView = Serialize(view);
                if (prior is not null)
                {
                    Execute("UPDATE state SET view_json=$v WHERE id=1", ("$v", terminalView)); tx.Commit(); return prior;
                }
                bool change = result.Decision.Outcome == BattleOutcome.AttackerWin;
                var receipt = new BattleReceipt(identity.BattleId, identity.Boundary, digest, result.Decision.Outcome,
                    result.Decision.Tick, change ? BattleSide.Attacker : state.Owner,
                    change ? checked(state.OwnerRevision + 1) : state.OwnerRevision, change ? 1 : 0);
                var phase = result.Decision.Outcome == BattleOutcome.TechnicalAbort ? BattlePhase.TechnicalAbort : BattlePhase.Applied;
                Execute("UPDATE state SET phase=$p,owner=$o,owner_revision=$r,owner_changes=$c,result_json=$j,receipt_json=$v,view_json=$view WHERE id=1",
                    ("$p", (int)phase), ("$o", (int)receipt.Owner), ("$r", receipt.OwnerRevision), ("$c", receipt.OwnerChangedCount),
                    ("$j", Serialize(result)), ("$v", Serialize(receipt)), ("$view", terminalView));
                tx.Commit(); return receipt;
            }
            finally { transaction = null; }
        }
    }

    public void RevokeExecutor()
    {
        lock (sync)
        {
            using var tx = db.BeginTransaction(); transaction = tx;
            try
            {
                var state = ReadState();
                Execute("UPDATE state SET fence=$f,phase=$p WHERE id=1", ("$f", checked(state.Fence + 1)),
                    ("$p", (int)(state.Phase == BattlePhase.Applied ? state.Phase : BattlePhase.TechnicalAbort)));
                tx.Commit();
            }
            finally { transaction = null; }
        }
    }

    public void RotateBoundary(Guid boundary)
    {
        if (boundary == Guid.Empty) throw new ArgumentException("empty boundary", nameof(boundary));
        lock (sync)
        {
            using var tx = db.BeginTransaction(); transaction = tx;
            try
            {
                var state = ReadState();
                if (boundary == state.Boundary) throw new InvalidDataException("restore boundary must be new");
                Execute("UPDATE state SET boundary=$b,fence=$f,phase=$p WHERE id=1", ("$b", boundary.ToString()),
                    ("$f", checked(state.Fence + 1)), ("$p", (int)(state.Phase == BattlePhase.Applied ? state.Phase : BattlePhase.TechnicalAbort)));
                tx.Commit();
            }
            finally { transaction = null; }
        }
    }

    public void Enqueue(BattleRole role, BattleIdentity requestIdentity, BattleCommand value, Func<bool> allowNew)
    {
        BattleSide side = SideOf(role);
        lock (sync)
        {
            using var tx = db.BeginTransaction(); transaction = tx;
            try
            {
                EnsureIdentity(requestIdentity);
                var start = ReadStart();
                if (ReadState().Phase != BattlePhase.Started) throw new InvalidDataException("battle is not running");
                if (value.Sequence <= 0 || value.Sequence > 24000 || string.IsNullOrEmpty(value.PawnId)
                    || !start.View.Pawns.Any(p => p.Id == value.PawnId && p.Side == side))
                    throw new InvalidDataException("invalid command owner or sequence");
                bool valid = value.Kind switch
                {
                    BattleCommandKind.MoveTo => Contains(start.View.Arena, value.X, value.Z) && value.TargetPawnId is null,
                    BattleCommandKind.Attack => value.X == 0 && value.Z == 0 && start.View.Pawns.Any(p => p.Id == value.TargetPawnId && p.Side != side),
                    BattleCommandKind.Draft => value.X == 0 && value.Z == 0 && value.TargetPawnId is null,
                    _ => false
                };
                if (!valid) throw new InvalidDataException("command is outside the battle allowlist");
                string digest = BattleContract.Digest(value);
                using (var prior = Command("SELECT digest FROM commands WHERE side=$s AND sequence=$n", ("$s", (int)side), ("$n", value.Sequence)))
                {
                    if (prior.ExecuteScalar() is string stored)
                    {
                        if (stored != digest) throw new InvalidDataException("sequence payload conflict");
                        tx.Commit(); return;
                    }
                }
                if (value.Sequence != NextSequence(side)) throw new InvalidDataException("sequence gap");
                using (var count = Command("SELECT COUNT(*) FROM commands WHERE side=$s AND ack_json IS NULL", ("$s", (int)side)))
                    if ((long)count.ExecuteScalar()! >= 64) throw new BattleRefusalException("QueueFull");
                if (!allowNew()) throw new BattleRefusalException("RateLimit");
                Execute("INSERT INTO commands(side,sequence,digest,command_json) VALUES($s,$n,$d,$c)",
                    ("$s", (int)side), ("$n", value.Sequence), ("$d", digest), ("$c", Serialize(value)));
                tx.Commit();
            }
            finally { transaction = null; }
        }
    }

    public void Acknowledge(BattleRole role, BattleIdentity requestIdentity, BattleCommandAck ack)
    {
        RequireArbiter(role);
        lock (sync)
        {
            using var tx = db.BeginTransaction(); transaction = tx;
            try
            {
                EnsureIdentity(requestIdentity);
                var start = ReadStart();
                if (ReadState().Phase != BattlePhase.Started || !Enum.IsDefined(ack.Side) || ack.Sequence <= 0
                    || ack.Tick < start.View.StartTick || ack.Tick - start.View.StartTick > BattleRules.MatchTicks
                    || !BattleContract.IsHash(ack.Digest) || !BattleContract.IsHash(ack.JournalHash)
                    || ack.Error?.Length > 256 || ack.Succeeded && ack.Error is not null)
                    throw new InvalidDataException("invalid command acknowledgement");
                string? prior;
                using (var query = Command("SELECT digest,ack_json FROM commands WHERE side=$s AND sequence=$n", ("$s", (int)ack.Side), ("$n", ack.Sequence)))
                using (var reader = query.ExecuteReader())
                {
                    if (!reader.Read() || reader.GetString(0) != ack.Digest) throw new InvalidDataException("unknown command acknowledgement");
                    prior = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
                string json = Serialize(ack);
                if (prior is not null && prior != json) throw new InvalidDataException("acknowledgement conflict");
                Execute("UPDATE commands SET ack_json=$a WHERE side=$s AND sequence=$n", ("$a", json), ("$s", (int)ack.Side), ("$n", ack.Sequence));
                tx.Commit();
            }
            finally { transaction = null; }
        }
    }

    public void Publish(BattleRole role, BattleView view)
    {
        RequireArbiter(role);
        lock (sync)
        {
            EnsureIdentity(view.Identity);
            var start = ReadStart(); string priorJson = Text("view_json")!; var prior = Deserialize<BattleView>(priorJson);
            ValidateRoster(view.Pawns, start.View.Arena);
            string json = Serialize(view);
            if (ReadState().Phase == BattlePhase.Started && view.Sequence == prior.Sequence && json == priorJson) return;
            if (ReadState().Phase != BattlePhase.Started || view.StartTick != start.View.StartTick || view.Arena != start.View.Arena
                || view.Zone != start.View.Zone || view.Sequence <= prior.Sequence || view.State is null
                || view.State.Tick < prior.State.Tick || view.State.Tick - view.StartTick > BattleRules.MatchTicks
                || view.State.HeldTicks is < 0 or > BattleRules.CaptureTicks || !Enum.IsDefined(view.State.Outcome)
                || !Enum.IsDefined(view.State.Reason) || prior.State.Outcome != BattleOutcome.Ongoing
                || view.Pawns.Any(p => !start.View.Pawns.Any(initial => initial.Id == p.Id && initial.Side == p.Side)))
                throw new InvalidDataException("invalid battle view");
            Execute("UPDATE state SET view_json=$v WHERE id=1", ("$v", json));
        }
    }

    public BattleWireReply Poll(BattleRole role, BattleIdentity requestIdentity)
    {
        lock (sync)
        {
            EnsureIdentity(requestIdentity);
            var state = ReadState();
            var pending = new List<QueuedBattleCommand>();
            var acks = new List<BattleCommandAck>();
            if (role == BattleRole.Arbiter && state.Phase == BattlePhase.Started)
            {
                using var query = Command("SELECT side,command_json,digest FROM commands WHERE ack_json IS NULL ORDER BY sequence,side");
                using var reader = query.ExecuteReader();
                while (reader.Read()) pending.Add(new((BattleSide)reader.GetInt32(0), Deserialize<BattleCommand>(reader.GetString(1)), reader.GetString(2)));
            }
            if (role != BattleRole.Arbiter)
            {
                using var query = Command("SELECT ack_json FROM commands WHERE side=$s AND ack_json IS NOT NULL ORDER BY sequence DESC LIMIT 64", ("$s", (int)SideOf(role)));
                using var reader = query.ExecuteReader();
                while (reader.Read()) acks.Add(Deserialize<BattleCommandAck>(reader.GetString(0)));
            }
            return new(true, "Ok", state, Text("view_json") is { } v ? Deserialize<BattleView>(v) : null,
                pending.ToArray(), Text("receipt_json") is { } r ? Deserialize<BattleReceipt>(r) : null,
                role == BattleRole.Arbiter ? 1 : NextSequence(SideOf(role)), acks.ToArray());
        }
    }

    private long NextSequence(BattleSide side)
    {
        using var query = Command("SELECT COALESCE(MAX(sequence),0)+1 FROM commands WHERE side=$s", ("$s", (int)side));
        return (long)query.ExecuteScalar()!;
    }
    private BattleStart ReadStart() => Text("start_json") is { } json ? Deserialize<BattleStart>(json) : throw new InvalidDataException("battle not started");
    private static BattleSide SideOf(BattleRole role) => role switch
    {
        BattleRole.SideA => BattleSide.Attacker, BattleRole.SideB => BattleSide.Defender,
        _ => throw new UnauthorizedAccessException("only a bound side supplies commands")
    };

    private void ValidateFinal(BattleStart start, BattleFinalResult result)
    {
        var d = result.Decision;
        ValidateRoster(result.Pawns, start.View.Arena);
        if (result.Pawns.Any(p => !start.View.Pawns.Any(initial => initial.Id == p.Id && initial.Side == p.Side))
            || result.Snapshot.Kind != BattleArtifactKind.FinalSnapshot || result.Journal.Kind != BattleArtifactKind.Journal
            || result.InitialSnapshotSha256 != start.Snapshot.Sha256 || d.Tick < start.View.StartTick
            || d.Tick - start.View.StartTick > BattleRules.MatchTicks || d.HeldTicks is < 0 or > BattleRules.CaptureTicks)
            throw new InvalidDataException("final result does not match its start");
        bool a = result.Pawns.Any(p => p.Side == BattleSide.Attacker && p.CombatReady);
        bool b = result.Pawns.Any(p => p.Side == BattleSide.Defender && p.CombatReady);
        bool zone = result.Pawns.Any(p => p.Side == BattleSide.Attacker && p.CombatReady && start.View.Zone.Contains(p))
            && !result.Pawns.Any(p => p.Side == BattleSide.Defender && p.CombatReady && start.View.Zone.Contains(p));
        bool valid = d.Outcome switch
        {
            BattleOutcome.Draw => !a && !b && d.Reason == BattleEndReason.MutualElimination,
            BattleOutcome.AttackerWin => a && ((!b && d.Reason == BattleEndReason.SideEliminated)
                || zone && d.HeldTicks == BattleRules.CaptureTicks && d.Reason == BattleEndReason.ZoneHeld),
            BattleOutcome.DefenderWin => b && ((!a && d.Reason == BattleEndReason.SideEliminated)
                || a && d.Tick - start.View.StartTick == BattleRules.MatchTicks && d.HeldTicks < BattleRules.CaptureTicks && d.Reason == BattleEndReason.TickLimit),
            BattleOutcome.TechnicalAbort => d.Reason is BattleEndReason.WallClockLimit or BattleEndReason.TransportLost or BattleEndReason.InvalidState,
            _ => false
        };
        if (!valid || d.Outcome != BattleOutcome.TechnicalAbort && d.Tick == start.View.StartTick)
            throw new InvalidDataException("incoherent terminal facts");
    }

    internal static void ValidateRoster(BattlePawn[] pawns, BattleZone arena)
    {
        if (pawns is null || pawns.Length != 8 || pawns.Any(p => p is null) || pawns.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != 8
            || pawns.Count(p => p.Side == BattleSide.Attacker) != 4 || pawns.Count(p => p.Side == BattleSide.Defender) != 4
            || pawns.Any(p => string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 64 || !float.IsFinite(p.Health)
                || p.Health is < 0 or > 1 || !Contains(arena, p.X, p.Z))) throw new InvalidDataException("invalid fixed battle roster");
    }
    internal static bool Contains(BattleZone zone, int x, int z) => x >= zone.MinX && x <= zone.MaxX && z >= zone.MinZ && z <= zone.MaxZ;

    private void EnsureIdentity(BattleIdentity value)
    {
        var state = ReadState();
        if (value != identity || state.Boundary != value.Boundary || state.Fence != value.Fence)
            throw new InvalidDataException("battle identity, executor or restore fence changed");
    }
    private static void RequireArbiter(BattleRole role) { if (role != BattleRole.Arbiter) throw new UnauthorizedAccessException("only the bound game arbiter supplies results"); }
    private void VerifyArtifact(BattleArtifact artifact)
    {
        if (!BattleContract.IsHash(artifact.Sha256) || artifact.Bytes is <= 0 or > 268435456) throw new InvalidDataException("invalid artifact descriptor");
        string path = ArtifactPath(artifact.Kind); CheckLinks(path);
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != artifact.Bytes || Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != artifact.Sha256)
                throw new InvalidDataException("artifact bytes do not match their committed hash");
        }
        catch (FileNotFoundException ex) { throw new InvalidDataException("battle artifact is missing", ex); }
    }
    public static string OwnedRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("stand must be a local absolute path");
        string full = Path.GetFullPath(path); CheckLinks(full); Directory.CreateDirectory(full); CheckLinks(full); return full;
    }
    private static void CheckLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("stand/artifact path contains a reparse alias");
    }
    private BattleLedgerState ReadState()
    {
        using var cmd = Command("SELECT identity_json,phase,owner,owner_revision,owner_changes,boundary,fence FROM state WHERE id=1 AND schema_version=1");
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("missing fixture state");
        return new(Deserialize<BattleIdentity>(reader.GetString(0)), (BattlePhase)reader.GetInt32(1), (BattleSide)reader.GetInt32(2),
            reader.GetInt64(3), reader.GetInt32(4), Guid.Parse(reader.GetString(5)), reader.GetInt64(6));
    }
    private string? Text(string column)
    {
        // Column names are fixed by this class, never supplied over the transport.
        using var cmd = Command($"SELECT {column} FROM state WHERE id=1"); return cmd.ExecuteScalar() as string;
    }
    private SqliteCommand Command(string sql, params (string Key, object Value)[] values)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value); return cmd;
    }
    private void Execute(string sql, params (string Key, object Value)[] values) { using var cmd = Command(sql, values); cmd.ExecuteNonQuery(); }
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, BattleContract.Json);
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, BattleContract.Json) ?? throw new InvalidDataException("missing fixture document");
    public void Dispose() => db.Dispose();
}

public sealed class BattleRefusalException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
