using Google.Protobuf;

namespace Rimlink.Protocol;

/// <summary>Immutable authority context; process identity is deliberately not part of it.</summary>
public sealed record OutpostScope(string WorldInstanceId, long OwnerPlayerId, long SaveGeneration,
    long SaveWriteEpoch, string RestoreBoundary);

/// <summary>Digest identifies exact raw save XML, never the compressed transport file.</summary>
public sealed record SnapshotProof(string SnapshotId, ByteString RawSha256, OutpostScope Scope);
