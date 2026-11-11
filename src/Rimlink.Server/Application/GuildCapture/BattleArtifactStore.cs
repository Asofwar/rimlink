using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record BattleArtifactDescriptor(int Version, string BattleId, string WorldId, string Boundary,
    string ExecutorId, long Fence, string Kind, long SizeBytes, string Sha256);
internal sealed record BattleArtifactProgress(long ReceivedBytes, bool Complete);
internal sealed record BattleArtifactProof(string RelativePath, long SizeBytes, string Sha256);

/// <summary>Caller owns S, then the battle lock. Staging is durable transport data; only a common transition can reference completion.</summary>
internal sealed class BattleArtifactStore(string archiveRoot)
{
    private readonly string root = Path.GetFullPath(archiveRoot);
    internal const int ChunkBytes = 256 * 1024;
    internal const long MaximumBytes = 64L * 1024 * 1024;

    internal BattleArtifactProgress AppendHeld(BattleArtifactDescriptor descriptor, long offset, ReadOnlySpan<byte> chunk)
    {
        var paths = Paths(descriptor);
        if (chunk.Length is < 1 or > ChunkBytes || offset < 0 || offset > descriptor.SizeBytes - chunk.Length) throw Invalid();
        byte[] intent = JsonSerializer.SerializeToUtf8Bytes(descriptor);
        if (!File.Exists(paths.Intent))
        {
            if (offset != 0 || File.Exists(paths.Part) || File.Exists(paths.Complete)) throw Invalid();
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Intent)!);
            using var record = new FileStream(paths.Intent, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            record.Write(intent); record.Flush(true);
        }
        RequireIntent(paths.Intent, intent);
        if (File.Exists(paths.Complete))
        {
            _ = ValidateCompleteHeld(descriptor);
            using var complete = new FileStream(paths.Complete, FileMode.Open, FileAccess.Read, FileShare.None);
            Compare(complete, offset, chunk);
            return new(descriptor.SizeBytes, true);
        }
        long received;
        using (var part = new FileStream(paths.Part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (part.Length > descriptor.SizeBytes || offset > part.Length) throw Invalid();
            int prior = (int)Math.Min(chunk.Length, part.Length - offset);
            Compare(part, offset, chunk[..prior]);
            if (prior < chunk.Length)
            {
                part.Position = part.Length; part.Write(chunk[prior..]); part.Flush(true);
            }
            received = part.Length;
        }
        if (received != descriptor.SizeBytes) return new(received, false);
        RequireBytes(paths.Part, descriptor);
        File.Move(paths.Part, paths.Complete, overwrite: false);
        _ = ValidateCompleteHeld(descriptor);
        return new(received, true);
    }

    internal BattleArtifactProof ValidateCompleteHeld(BattleArtifactDescriptor descriptor)
    {
        var paths = Paths(descriptor); RequireIntent(paths.Intent, JsonSerializer.SerializeToUtf8Bytes(descriptor));
        if (!File.Exists(paths.Complete)) throw Invalid();
        RequireBytes(paths.Complete, descriptor);
        return new(Path.GetRelativePath(root, paths.Complete).Replace('\\', '/'), descriptor.SizeBytes, descriptor.Sha256);
    }

    // The authenticated consumer validates the assembled raw hash; each page is read under the same storage/battle fence.
    internal byte[] ReadHeld(BattleArtifactDescriptor descriptor, long offset)
    {
        var paths = Paths(descriptor); RequireIntent(paths.Intent, JsonSerializer.SerializeToUtf8Bytes(descriptor));
        if (offset < 0 || offset >= descriptor.SizeBytes || !File.Exists(paths.Complete)) throw Invalid();
        using var file = new FileStream(paths.Complete, FileMode.Open, FileAccess.Read, FileShare.None);
        if (file.Length != descriptor.SizeBytes) throw Invalid();
        byte[] result = new byte[(int)Math.Min(ChunkBytes, descriptor.SizeBytes - offset)];
        file.Position = offset; file.ReadExactly(result); return result;
    }

    internal byte[] ReadDocumentHeld(BattleArtifactDescriptor descriptor,int maximum)
    {
        if (maximum < 1 || descriptor.SizeBytes > maximum) throw Invalid();
        var paths = Paths(descriptor); ValidateCompleteHeld(descriptor);
        using var file = new FileStream(paths.Complete,FileMode.Open,FileAccess.Read,FileShare.None);
        byte[] bytes = new byte[checked((int)descriptor.SizeBytes)]; file.ReadExactly(bytes); return bytes;
    }

    private (string Intent, string Part, string Complete) Paths(BattleArtifactDescriptor d)
    {
        if (d is null || d.Version != 1 || !Uuid(d.BattleId) || !Uuid(d.Boundary) || d.Fence <= 0 || d.SizeBytes is < 1 or > MaximumBytes
            || string.IsNullOrWhiteSpace(d.WorldId) || d.WorldId.Length > 256 || d.WorldId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(d.ExecutorId) || d.ExecutorId.Length > 128 || d.ExecutorId.Any(char.IsControl)
            || !Kind(d.Kind)
            || d.Sha256 is not { Length: 64 } || d.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();
        if ((d.Kind.StartsWith("start-",StringComparison.Ordinal) && d.SizeBytes > 8192) || d.Kind == "view" && d.SizeBytes > 1048576 || d.Kind.StartsWith("offer-",StringComparison.Ordinal) && d.SizeBytes>8192
            || d.Kind is "transport" or "result" && d.SizeBytes > 16384) throw Invalid();
        return PathsForIdentity(d.BattleId,d.Fence,d.Kind);
    }
    private (string Intent,string Part,string Complete) PathsForIdentity(string battleId,long fence,string kind)
    {
        if (!Uuid(battleId) || fence <= 0 || !Kind(kind)) throw Invalid();
        string stem = Path.Combine(root,"guild-battles",battleId,fence.ToString(CultureInfo.InvariantCulture),kind);
        foreach (string suffix in new[] { ".intent", ".part", ".artifact" }) RequirePath(stem + suffix);
        return (stem + ".intent",stem + ".part",stem + ".artifact");
    }
    internal string CommandDirectoryHeld(string battleId,long fence)
    {
        var paths = PathsForIdentity(battleId,fence,"transport");
        string directory = Path.Combine(Path.GetDirectoryName(paths.Complete)!,"commands");
        RequirePath(Path.Combine(directory,battleId + ".commands")); return directory;
    }
    private static void RequirePath(string path)
    {
        for (string? current = path;current is not null;current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
    }
    internal BattleArtifactDescriptor? DescriptorHeld(string battleId,long fence,string kind)
    {
        var paths = PathsForIdentity(battleId,fence,kind);
        if (!File.Exists(paths.Intent))
        {
            if (File.Exists(paths.Part) || File.Exists(paths.Complete)) throw Invalid();
            return null;
        }
        if (new FileInfo(paths.Intent).Length is < 1 or > 4096) throw Invalid();
        byte[] bytes = File.ReadAllBytes(paths.Intent);
        BattleArtifactDescriptor descriptor;
        try { descriptor = JsonSerializer.Deserialize<BattleArtifactDescriptor>(bytes,new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
        if (descriptor.BattleId != battleId || descriptor.Fence != fence || descriptor.Kind != kind
            || !JsonSerializer.SerializeToUtf8Bytes(descriptor).AsSpan().SequenceEqual(bytes)) throw Invalid();
        _ = Paths(descriptor); return descriptor;
    }
    private static bool Kind(string value) => value is "initial" or "final" or "simulation" or "transport" or "view" or "result"
        or "start-1" or "start-2" or "start-3" or "start-4" or "start-5" or "start-6" or "start-7" or "start-8" || IndexedKind(value,"offer-",64) || IndexedKind(value,"offer-ack-",64);
    private static bool IndexedKind(string value,string prefix,int maximum)=>value.StartsWith(prefix,StringComparison.Ordinal)
        && int.TryParse(value.AsSpan(prefix.Length),System.Globalization.NumberStyles.None,CultureInfo.InvariantCulture,out int index) && index>=1 && index<=maximum
        && value[prefix.Length..]==index.ToString(CultureInfo.InvariantCulture);

    private static void RequireIntent(string path, byte[] expected)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Length || expected.Length > 4096
            || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected)) throw Invalid();
    }
    private static void RequireBytes(string path, BattleArtifactDescriptor descriptor)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (file.Length != descriptor.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(file)) != descriptor.Sha256) throw Invalid();
    }
    private static void Compare(FileStream file, long offset, ReadOnlySpan<byte> expected)
    {
        byte[] bytes = new byte[expected.Length]; file.Position = offset; file.ReadExactly(bytes);
        if (!bytes.AsSpan().SequenceEqual(expected)) throw Invalid();
    }
    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    private static InvalidDataException Invalid() => new("battle-artifact-evidence-refused");
}
