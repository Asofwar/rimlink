using System.Text.Json;
using System.Text.Json.Serialization;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Backup;

public sealed record OutpostBackupIdentity(uint FormatVersion, string InstallationId, string WorldId, string RestoreBoundary,
    string HeadId, byte[] HeadDigest, string TransitionId, string State);
public sealed record OutpostBackupFile(string Path, long Length, byte[] Sha256);
public sealed record OutpostBackupProof(string Kind, string Id, byte[] Sha256);
public sealed record OutpostBackupDomain(int Version, OutpostDomainFormat Format, OutpostSchemaBinding Schema,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Application.GuildCapture.GuildAcceptanceProvenance? Acceptance=null);
public sealed record OutpostBackupManifest(int Version, OutpostBackupIdentity Authority, DateTime CutUtc, byte[] DomainDigest,
    OutpostBackupFile[] Files, OutpostBackupProof[] Proofs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OutpostBackupDomain? Domain = null)
{
    public const string FileName = "outpost-manifest.json";
    internal const int MaximumBytes = 32 * 1024 * 1024;
    internal const int MaximumFiles = 100000;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };

    // Keep the exact six-field legacy reader. Adding Domain to its accepted shape would silently upgrade old evidence.
    private sealed record Legacy(int Version, OutpostBackupIdentity Authority, DateTime CutUtc, byte[] DomainDigest,
        OutpostBackupFile[] Files, OutpostBackupProof[] Proofs);

    internal static OutpostBackupManifest Decode(byte[] bytes)
    {
        if (bytes is null || bytes.Length is 0 or > MaximumBytes) throw Invalid();
        byte[] owned = bytes.ToArray();
        try
        {
            using var document = JsonDocument.Parse(owned, new JsonDocumentOptions { MaxDepth = Json.MaxDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var versions = document.RootElement.EnumerateObject().Where(p => string.Equals(p.Name, "version", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (versions.Length != 1 || !versions[0].Value.TryGetInt32(out int version)) throw Invalid();
            OutpostBackupManifest result;
            if (version == 1)
            {
                var original = JsonSerializer.Deserialize<Legacy>(owned, Json) ?? throw Invalid();
                result = new(original.Version, original.Authority, original.CutUtc, original.DomainDigest, original.Files, original.Proofs);
            }
            else if (version is 2 or 3) result = JsonSerializer.Deserialize<OutpostBackupManifest>(owned, Json) ?? throw Invalid();
            else throw Invalid();
            result.ValidateShape(); return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("backup-manifest-invalid", error); }
    }

    internal void ValidateDomain(OutpostAuthority.StoredDomainObservation observed, Application.GuildCapture.GuildAcceptanceProvenance? selectedAcceptance = null)
    {
        ValidateShape();
        if (Domain?.Acceptance != selectedAcceptance || observed.Acceptance != selectedAcceptance)
            throw GuildAcceptanceRestorePermit.Invalid();
        if (!DomainDigest.AsSpan().SequenceEqual(observed.Digest.Span)
            || Version == 1 && observed.Format != OutpostDomainFormat.V1
            || Version is 2 or 3 && (Domain!.Format != observed.Format || Domain.Schema.ProfileId != observed.Schema.ProfileId
                || !Domain.Schema.Digest.AsSpan().SequenceEqual(observed.Schema.Digest.Span)))
            throw new InvalidDataException("restore-cut-domain-binding-mismatch");
    }

    private void ValidateShape()
    {
        if (Authority is null || CutUtc.Kind != DateTimeKind.Utc || DomainDigest is not { Length: 32 }
            || Files is null || Proofs is null || Files.Length > MaximumFiles || Proofs.Length > MaximumFiles
            || Version == 1 && Domain is not null || Version is not (1 or 2 or 3)) throw Invalid();
        if(Domain?.Acceptance is{} acceptance)
        {
            try{Application.GuildCapture.GuildAuthorityEvidence.ValidateAcceptanceIdentity(acceptance);}catch(OutpostEvidenceUnavailableException){throw Invalid();}
            if(Version!=3 || acceptance.WorldId!=Authority.WorldId || acceptance.Boundary!=Authority.RestoreBoundary)throw Invalid();
        }
        if (Version == 2 && (Domain is null || Domain.Version != 1 || Domain.Format is not (OutpostDomainFormat.V1 or OutpostDomainFormat.V2)
            || !OutpostDomainSchema.IsKnownBinding(Domain.Schema, current: Domain.Format == OutpostDomainFormat.V2))) throw Invalid();
        if (Version == 3 && (Domain is null || Domain.Version != 2 || Domain.Format != OutpostDomainFormat.V3
            || !OutpostDomainSchema.HasGuildTables(new(Domain.Schema.ProfileId, Google.Protobuf.ByteString.CopyFrom(Domain.Schema.Digest), true)))) throw Invalid();
    }

    internal byte[] Encode()
    {
        ValidateShape();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        if (bytes.Length > MaximumBytes) throw new OutpostEvidenceUnavailableException("backup-manifest-limit");
        return bytes;
    }

    private static InvalidDataException Invalid() => new("backup-manifest-invalid");
}
