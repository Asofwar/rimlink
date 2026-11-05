using System.Text.Json;
using System.Text.Json.Serialization;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Backup;

public sealed record OutpostBackupIdentity(uint FormatVersion, string InstallationId, string WorldId, string RestoreBoundary,
    string HeadId, byte[] HeadDigest, string TransitionId, string State);
public sealed record OutpostBackupFile(string Path, long Length, byte[] Sha256);
public sealed record OutpostBackupProof(string Kind, string Id, byte[] Sha256);
public sealed record OutpostBackupManifest(int Version, OutpostBackupIdentity Authority, DateTime CutUtc, byte[] DomainDigest,
    OutpostBackupFile[] Files, OutpostBackupProof[] Proofs)
{
    public const string FileName = "outpost-manifest.json";
    internal const int MaximumBytes = 32 * 1024 * 1024;
    internal const int MaximumFiles = 100000;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };
    internal byte[] Encode()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        if (bytes.Length > MaximumBytes || Files.Length > MaximumFiles || Proofs.Length > MaximumFiles)
            throw new OutpostEvidenceUnavailableException("backup-manifest-limit");
        return bytes;
    }
}
