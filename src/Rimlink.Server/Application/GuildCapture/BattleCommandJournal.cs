using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record BattleJournalScope(string BattleId, string WorldId, string Boundary, string ExecutorId, long Fence, byte[] StartDigest);
internal sealed record BattleJournalCommand(long Sequence, int Side, long ActorId, string RequestId, byte[] Command,
    byte[]? Acknowledgement = null, long? AcceptedTick = null, BattleJournalControl? Control = null, string? DeliveryId = null);
internal sealed record BattleJournalSeal(byte[] Digest, long SizeBytes);

/// <summary>Transport evidence only. Every call owns S, then the battle lock; no file handle survives the call.</summary>
internal sealed partial class BattleCommandJournal
{
    internal const long MaximumBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string path;
    private readonly byte[] scopeBytes;
    private readonly Dictionary<Guid, BattleJournalCommand> requests = [];
    private readonly Dictionary<(int Side, long Sequence), BattleJournalCommand> commands = [];
    private readonly long[] sequence = new long[2];
    private readonly int[] pending = new int[2];
    private byte[] tail = new byte[32];
    private long length;
    private bool sealedJournal, poisoned;

    private BattleCommandJournal(string path, byte[] scopeBytes) { this.path = path; this.scopeBytes = scopeBytes; }

    internal static BattleCommandJournal OpenHeld(string directory, BattleJournalScope scope, bool createIfMissing = true)
    {
        if (scope is null || !Guid.TryParseExact(scope.BattleId, "D", out _) || !Guid.TryParseExact(scope.Boundary, "D", out _)
            || string.IsNullOrWhiteSpace(scope.WorldId) || scope.WorldId.Length > 256 || string.IsNullOrWhiteSpace(scope.ExecutorId)
            || scope.ExecutorId.Length > 128 || scope.Fence < 1 || scope.StartDigest is not { Length: 32 }) throw Invalid();
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(scope, Json);
        var journal = new BattleCommandJournal(Path.Combine(directory, scope.BattleId + ".commands"), header);
        if (!File.Exists(journal.path))
        {
            if (!createIfMissing) throw Invalid();
            Directory.CreateDirectory(directory);
            using var file = new FileStream(journal.path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write("RLGBCJ01"u8);
            journal.length = 8;
            journal.WriteFrame(file, [0, .. header]);
        }
        journal.Replay();
        return journal;
    }

    internal long Append(int side, long actor, string request, ReadOnlySpan<byte> command)
    {
        int index = Side(side);
        if (actor <= 0 || command.Length is < 1 or > 16384 || !Request(request, out var id)) throw Invalid();
        RequireHealthy();
        if (requests.TryGetValue(id, out var prior))
        {
            if (prior.Control is not null || prior.Side != side || prior.ActorId != actor || !prior.Command.AsSpan().SequenceEqual(command)) throw Invalid();
            return prior.Sequence;
        }
        if (sealedJournal || pending[index] >= 64 || sequence[index] >= 24000) throw Invalid();
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)1); writer.Write((byte)side); writer.Write(actor); writer.Write(id.ToByteArray());
        writer.Write(sequence[index] + 1); writer.Write(command.Length); writer.Write(command);
        byte[] bytes = buffer.ToArray();
        AppendFrame(bytes);
        AcceptFrame(bytes);
        return sequence[index];
    }

    // A null tick records terminal rejection without claiming that the native game executed the command.
    internal void Acknowledge(int side, long sequence, long? tick, ReadOnlySpan<byte> result)
    {
        Side(side); RequireHealthy();
        if (tick is < 0 or > 30000 || result.Length is < 1 or > 16384 || !commands.TryGetValue((side, sequence), out var prior)) throw Invalid();
        if (prior.Acknowledgement is not null)
        {
            if (prior.AcceptedTick != tick || !prior.Acknowledgement.AsSpan().SequenceEqual(result)) throw Invalid();
            return;
        }
        if (sealedJournal) throw Invalid();
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)2); writer.Write((byte)side); writer.Write(sequence); writer.Write(tick ?? -1);
        writer.Write(result.Length); writer.Write(result);
        byte[] bytes = buffer.ToArray();
        AppendFrame(bytes); AcceptFrame(bytes);
    }

    internal BattleJournalCommand Read(int side, long sequence)
    {
        Side(side); RequireHealthy();
        if (!commands.TryGetValue((side, sequence), out var command)) throw Invalid();
        return command with { Command = command.Command.ToArray(), Acknowledgement = command.Acknowledgement?.ToArray(), Control = command.Control is null ? null : command.Control with { SessionDigest = command.Control.SessionDigest.ToArray() } };
    }

    internal BattleJournalCommand? FindRequest(string request)
    {RequireHealthy();if(!Request(request,out var id))throw Invalid();return requests.TryGetValue(id,out var prior)?Read(prior.Side,prior.Sequence):null;}

    internal long LastSequence(int side) { RequireHealthy(); return sequence[Side(side)]; }

    internal int Pending(int side) { RequireHealthy(); return pending[Side(side)]; }

    internal BattleJournalSeal Seal()
    {
        RequireHealthy();
        // Validate the complete bytes once at the financial boundary, never once per command.
        Replay();
        if (!sealedJournal)
        {
            if (pending.Any(p => p != 0) || pendingCut.Any(c => c is not null)) throw Invalid();
            AppendFrame([3]); AcceptFrame([3]);
        }
        using var file = File.OpenRead(path);
        return new(SHA256.HashData(file), file.Length);
    }

    internal BattleJournalSeal ValidateSeal()
    {
        RequireHealthy(); Replay();
        if (!sealedJournal) throw Invalid();
        using var file = File.OpenRead(path); return new(SHA256.HashData(file),file.Length);
    }

    private void Replay()
    {
        requests.Clear(); commands.Clear(); Array.Clear(sequence); Array.Clear(pending);
        tail = new byte[32]; sealedJournal = false; runtimeStarted=null; ResetTransport();
        using var file = File.OpenRead(path);
        if (file.Length is < 45 or > MaximumBytes) throw Invalid();
        Span<byte> magic = stackalloc byte[8]; file.ReadExactly(magic);
        if (!magic.SequenceEqual("RLGBCJ01"u8)) throw Invalid();
        bool first = true;
        Span<byte> size = stackalloc byte[4];
        Span<byte> stored = stackalloc byte[32];
        try
        {
            while (file.Position < file.Length)
            {
                file.ReadExactly(size);
                int count = BinaryPrimitives.ReadInt32LittleEndian(size);
                if (count is < 1 or > 16448 || file.Length - file.Position < count + 32) throw Invalid();
                byte[] record = new byte[count]; file.ReadExactly(record); file.ReadExactly(stored);
                byte[] digest = HashFrame(tail, record);
                if (!CryptographicOperations.FixedTimeEquals(digest, stored)) throw Invalid();
                if (first)
                {
                    if (record[0] != 0 || !record.AsSpan(1).SequenceEqual(scopeBytes)) throw Invalid();
                    first = false;
                }
                else AcceptFrame(record);
                tail = digest;
            }
            length = file.Length;
        }
        catch (EndOfStreamException error) { poisoned = true; throw new InvalidDataException("battle-journal-invalid", error); }
        catch { poisoned = true; throw; }
    }

    private void AcceptFrame(byte[] bytes)
    {
        if (sealedJournal) throw Invalid();
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        byte kind = reader.ReadByte();
        if (kind == 3)
        {
            if (bytes.Length != 1 || pending.Any(p => p != 0) || pendingCut.Any(c => c is not null)) throw Invalid();
            sealedJournal = true; position++; return;
        }
        if(kind==9){AcceptRuntimeStarted(reader);position++;return;}
        if (kind is >= 4 and <= 8) { AcceptTransportFrame(kind,reader); position++; return; }
        int side = reader.ReadByte(), index = Side(side);
        if (kind == 1)
        {
            long actor = reader.ReadInt64();
            var id = new Guid(ReadExactly(reader, 16));
            long next = reader.ReadInt64();
            byte[] body = ReadBody(reader);
            if (actor <= 0 || id == Guid.Empty || requests.ContainsKey(id) || next != sequence[index] + 1
                || next > 24000 || pending[index] >= 64) throw Invalid();
            var command = new BattleJournalCommand(next, side, actor, id.ToString("N"), body);
            requests.Add(id, command); commands.Add((side, next), command);
            sequence[index] = next; pending[index]++; unacknowledged.Add(new(side,next));
        }
        else if (kind == 2)
        {
            long next = reader.ReadInt64(), tick = reader.ReadInt64();
            byte[] body = ReadBody(reader);
            if (tick is < -1 or > 30000 || !commands.TryGetValue((side, next), out var prior) || prior.Acknowledgement is not null) throw Invalid();
            var accepted = prior with { Acknowledgement = body, AcceptedTick = tick == -1 ? null : tick };
            commands[(side, next)] = accepted; requests[Guid.ParseExact(prior.RequestId, "N")] = accepted; pending[index]--;
            unacknowledged.Remove(new(side,next)); AfterTransportAcknowledgement();
        }
        else throw Invalid();
        if (reader.BaseStream.Position != bytes.Length) throw Invalid();
        position++;
    }

    private void AppendFrame(byte[] record)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (file.Length != length) throw Invalid();
            file.Position = file.Length - 32;
            Span<byte> previous = stackalloc byte[32]; file.ReadExactly(previous);
            if (!CryptographicOperations.FixedTimeEquals(previous, tail)) throw Invalid();
            WriteFrame(file, record);
        }
        catch { poisoned = true; throw; }
    }

    private void WriteFrame(FileStream file, byte[] record)
    {
        // Keep one final seal record available even at the aggregate transport budget.
        if (length + record.Length + 36 > MaximumBytes - (record[0] == 3 ? 0 : 37)) throw Invalid();
        byte[] digest = HashFrame(tail, record);
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, record.Length);
        file.Write(size); file.Write(record); file.Write(digest); file.Flush(flushToDisk: true);
        length = file.Position; tail = digest;
    }

    private static byte[] HashFrame(byte[] previous, byte[] record)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(previous); hash.AppendData(record); return hash.GetHashAndReset();
    }

    private static byte[] ReadBody(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length is < 1 or > 16384) throw Invalid();
        return ReadExactly(reader, length);
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        return bytes.Length == count ? bytes : throw Invalid();
    }

    private void RequireHealthy() { if (poisoned) throw Invalid(); }
    private static int Side(int side) => side is 1 or 2 ? side - 1 : throw Invalid();
    private static bool Request(string request, out Guid id) => (Guid.TryParseExact(request, "N", out id)
        || Guid.TryParseExact(request, "D", out id)) && id != Guid.Empty;
    private static InvalidDataException Invalid() => new("battle-journal-invalid");
}
