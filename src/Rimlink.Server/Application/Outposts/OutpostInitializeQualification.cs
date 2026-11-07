using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WorldEntity = Rimlink.Server.Storage.Entities.World;

namespace Rimlink.Server.Application.Outposts;

public sealed record OutpostInitializeFileSystemFacts(string Mount, string Kind);
public sealed record OutpostInitializeBackendFacts(string NativeVersion, string SourceId, string Vfs,
    byte[] NativeSha256, string ManagedProviderIdentity, string ManagedProviderVersion,
    string PclProviderIdentity, string PclProviderVersion);
public sealed record OutpostInitializeStorageFacts(string Contract, int AdmissionRevision,
    string DataDirectory, string MainDatabase, string AnchorDirectory, string AuthorityDatabase,
    OutpostInitializeFileSystemFacts DataFileSystem, OutpostInitializeFileSystemFacts AnchorFileSystem,
    int AnchorApplicationId, int AnchorUserVersion, int AnchorSingletonId, int AnchorLayoutVersion,
    OutpostInitializeBackendFacts AnchorBackend, OutpostInitializeBackendFacts DomainBackend,
    OutpostSchemaBinding Schema);

/// <summary>Stable observed facts only. Logical NULL/history absence is separately bound by Expected.Uninitialized.</summary>
public static class OutpostInitializeQualification
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public const int AdmissionRevision = 1;
    public const int MaximumQualificationBytes = 64 * 1024;

    // Fixed order, int32/int64 LE and length-prefixed exact UTF-8/binary. No current anchor payload or physical file hash.
    public static byte[] Encode(OutpostInitializeStorageFacts facts)
    {
        if (facts is null || facts.Contract != "sqlite-local-v1" || facts.AdmissionRevision != AdmissionRevision
            || facts.AnchorApplicationId != 1380732737 || facts.AnchorUserVersion != 1
            || facts.AnchorSingletonId != 1 || facts.AnchorLayoutVersion != 1
            || !OutpostDomainSchema.IsKnownBinding(facts.Schema, current: true)) throw Invalid();
        using var w = new Writer(MaximumQualificationBytes);
        w.Text("Rimlink.Outposts.InitializeQualification/v1", 128);
        w.Text(facts.Contract, 64); w.Number(facts.AdmissionRevision);
        w.Text(facts.DataDirectory, 4096); w.Text(facts.MainDatabase, 4096);
        w.Text(facts.AnchorDirectory, 4096); w.Text(facts.AuthorityDatabase, 4096);
        FileSystem(w, 1, facts.DataFileSystem); FileSystem(w, 2, facts.AnchorFileSystem);
        w.Number(facts.AnchorApplicationId); w.Number(facts.AnchorUserVersion);
        w.Number(facts.AnchorSingletonId); w.Number(facts.AnchorLayoutVersion);
        Backend(w, 1, facts.AnchorBackend); Backend(w, 2, facts.DomainBackend);
        w.Text(facts.Schema.ProfileId, 256); w.Hash(facts.Schema.Digest);
        return w.Bytes();
    }

    public static byte[] Digest(OutpostInitializeStorageFacts facts) => SHA256.HashData(Encode(facts));

    public static byte[] EncodeWorld(WorldEntity world, string effectiveWorld)
    {
        if (world is null || world.Id <= 0 || world.InfluenceRevision < 0 || world.CreatedAt.Ticks <= 0
            || string.IsNullOrWhiteSpace(world.Seed) || string.IsNullOrWhiteSpace(effectiveWorld)
            || world.InfluenceInstanceId != effectiveWorld || world.Params is not { Length: > 0 }
            || world.Params.Length > Rimlink.Protocol.Limits.MaxMessageBytes) throw Invalid();
        using var w = new Writer(3 * Rimlink.Protocol.Limits.MaxMessageBytes + 4096);
        w.Text("Rimlink.Outposts.InitializeWorldBinding/v1", 128);
        w.Long(world.Id); w.Text(world.Seed, Rimlink.Protocol.Limits.MaxMessageBytes);
        w.Blob(world.Params); w.Text(world.NpcTiles, Rimlink.Protocol.Limits.MaxMessageBytes, allowEmpty: true);
        // SQLite stores ticks; DateTime.Kind and conversions to UTC must not rewrite the observed projection.
        w.Long(world.CreatedAt.Ticks); w.Text(world.InfluenceInstanceId, 256);
        w.Long(world.InfluenceRevision); w.Text(effectiveWorld, 256);
        return w.Bytes();
    }

    public static byte[] WorldDigest(WorldEntity world, string effectiveWorld) => SHA256.HashData(EncodeWorld(world, effectiveWorld));

    private static void FileSystem(Writer w, int role, OutpostInitializeFileSystemFacts facts)
    {
        if (facts is null) throw Invalid();
        w.Number(role); w.Text(facts.Mount, 4096); w.Text(facts.Kind, 64);
    }

    private static void Backend(Writer w, int role, OutpostInitializeBackendFacts facts)
    {
        if (facts is null) throw Invalid();
        w.Number(role); w.Text(facts.NativeVersion, 64); w.Text(facts.SourceId, 256); w.Text(facts.Vfs, 64);
        w.Hash(facts.NativeSha256); w.Text(facts.ManagedProviderIdentity, 256); w.Text(facts.ManagedProviderVersion, 64);
        w.Text(facts.PclProviderIdentity, 256); w.Text(facts.PclProviderVersion, 64);
    }

    private sealed class Writer(int maximum) : IDisposable
    {
        private readonly MemoryStream stream = new();
        private void Raw(ReadOnlySpan<byte> bytes)
        {
            if (stream.Length + bytes.Length > maximum) throw Invalid();
            stream.Write(bytes);
        }
        public void Number(int number) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, number); Raw(bytes); }
        public void Long(long number) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, number); Raw(bytes); }
        public void Blob(ReadOnlySpan<byte> bytes) { Number(bytes.Length); Raw(bytes); }
        public void Hash(byte[] bytes) { if (bytes is not { Length: 32 }) throw Invalid(); Blob(bytes); }
        public void Text(string value, int maximumBytes, bool allowEmpty = false)
        {
            if (value is null || value.Length > maximumBytes || (!allowEmpty && string.IsNullOrWhiteSpace(value))) throw Invalid();
            try
            {
                if (Utf8.GetByteCount(value) > maximumBytes) throw Invalid();
                Blob(Utf8.GetBytes(value));
            }
            catch (EncoderFallbackException error) { throw Invalid(error); }
        }
        public byte[] Bytes() => stream.ToArray();
        public void Dispose() => stream.Dispose();
    }
    private static InvalidDataException Invalid(Exception? error = null) => new("outpost-initialize-qualification-invalid", error);
}
