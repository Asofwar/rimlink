using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

/// <summary>Bounded versioned metadata belonging to one exact XML snapshot.</summary>
[JsonConverter(typeof(OutpostRecoveryData.JsonConverter))]
public sealed record OutpostRecoveryData
{
    public const string SectionNode = "rimlinkOutposts";
    public const uint CurrentVersion = 1;
    public const int MaxMetadataBytes = 4 * 1024 * 1024;
    public const int MaxEntries = 10_000;
    private const int MaxBase64Chars = ((MaxMetadataBytes + 2) / 3) * 4;
    public OutpostSaveMetadata Metadata { get; init; } = new();
    public bool Present => Metadata.SchemaVersion != 0;

    public static OutpostRecoveryData ReadSection(XmlReader reader)
    {
        if (reader.NodeType != XmlNodeType.Element || reader.Name != SectionNode || reader.HasAttributes || reader.IsEmptyElement)
            throw Invalid();
        using var section = reader.ReadSubtree();
        string? version = null, encoded = null;
        while (section.Read())
        {
            if (section.NodeType == XmlNodeType.Element && section.Depth == 0) continue;
            if (section.NodeType is XmlNodeType.EndElement or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace) continue;
            if (section.NodeType != XmlNodeType.Element || section.Depth != 1 || section.HasAttributes) throw Invalid();
            if (section.Name == "version" && version is null) version = ReadScalar(section, 10);
            else if (section.Name == "metadata" && encoded is null) encoded = ReadScalar(section, MaxBase64Chars);
            else throw Invalid();
        }
        if (version != "1" || encoded is null) throw Invalid();
        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > MaxMetadataBytes) throw Invalid();
            using var input = CodedInputStream.CreateWithLimits(new MemoryStream(bytes, writable: false), MaxMetadataBytes, 32);
            var result = new OutpostRecoveryData { Metadata = OutpostSaveMetadata.Parser.ParseFrom(input) };
            result.Validate();
            if (!result.Present) throw Invalid();
            return result;
        }
        catch (Exception ex) when (ex is FormatException or InvalidProtocolBufferException)
        { throw new InvalidDataException("invalid outpost recovery metadata", ex); }
    }

    public void Validate()
    {
        if (Metadata.CalculateSize() > MaxMetadataBytes) throw Invalid();
        if (!Present && Metadata.CalculateSize() == 0) return;
        if (Metadata.SchemaVersion != CurrentVersion) throw Invalid();
        ValidateScope(Metadata.Scope);
        if (Metadata.Registry.Count > MaxEntries || Metadata.Receipts.Count > MaxEntries || Metadata.Frontier.Count > MaxEntries) throw Invalid();
        var sourceScopes = ValidateSourceBridge();
        ValidateAdmissions();
        ValidateRollbackBridge();
        if (Metadata.OrdinaryOutgoing.Count > MaxEntries) throw Invalid();
        var operations = new HashSet<string>(StringComparer.Ordinal);
        var ordinaryRequests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capture in Metadata.OrdinaryOutgoing)
        {
            OrdinaryCapture.Validate(capture);
            if (!CaptureScopeAllowed(capture.Scope, sourceScopes) || !operations.Add(capture.LocalOperationId)
                || !ordinaryRequests.Add(((int)capture.RequestKind) + ":" + capture.ClientRequestId)) throw Invalid();
        }
        var custody = new HashSet<string>(StringComparer.Ordinal);
        var liveReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Metadata.Registry)
        {
            if (entry.SchemaVersion != CurrentVersion || !Uuid(entry.PawnCustodyId) || !OptionalUuid(entry.AssignmentId)
                || !custody.Add(entry.PawnCustodyId) || entry.PawnReferenceToken.Length > 256
                || entry.PawnReferenceToken.Any(char.IsControl)
                || (!entry.Tombstone && (entry.PawnReferenceToken.Length == 0 || !liveReferences.Add(entry.PawnReferenceToken)))) throw Invalid();
            ValidateScope(entry.Scope);
        }
        var receipts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in Metadata.Receipts)
        {
            if (!Uuid(receipt.TransitionId) || !receipts.Add(receipt.TransitionId) || !OptionalUuid(receipt.PredecessorId)
                || !Uuid(receipt.ClientRequestId) || !Hash(receipt.TransitionDigest) || !Hash(receipt.IntentDigest)
                || !OptionalUuid(receipt.DeliveryAttemptId) || !OptionalUuid(receipt.NoRemovalBranchId) || !Enum.IsDefined(receipt.CancellationKind)
                || receipt.Snapshot is null || !Guid.TryParseExact(receipt.Snapshot.SnapshotId, "N", out _)
                || !Hash(receipt.Snapshot.RawSha256) || receipt.PawnCustodyIds.Count > MaxEntries || receipt.AssignmentIds.Count > MaxEntries
                || receipt.ParcelIds.Count > MaxEntries || receipt.PawnCustodyIds.Any(id => !Uuid(id))
                || receipt.AssignmentIds.Any(id => !Uuid(id)) || receipt.ParcelIds.Any(id => id <= 0)
                || receipt.PawnCustodyIds.Distinct(StringComparer.Ordinal).Count() != receipt.PawnCustodyIds.Count
                || receipt.AssignmentIds.Distinct(StringComparer.Ordinal).Count() != receipt.AssignmentIds.Count
                || receipt.ParcelIds.Distinct().Count() != receipt.ParcelIds.Count
                || !OptionalUuid(receipt.OrdinaryReturnAttemptId)
                || (receipt.OrdinaryReturnAttemptId.Length == 0) != (receipt.OrdinaryLocalOperationId.Length == 0)
                || receipt.OrdinaryLocalOperationId.Length > 0 && (!Guid.TryParseExact(receipt.OrdinaryLocalOperationId, "N", out var local)
                    || local.ToString("N") != receipt.OrdinaryLocalOperationId)) throw Invalid();
            ValidateScope(receipt.Snapshot.Scope);
        }
        var frontier = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Metadata.Frontier)
            if (!Uuid(entry.TransitionId) || !frontier.Add(entry.TransitionId) || !OptionalUuid(entry.PredecessorId) || !Hash(entry.TransitionDigest)) throw Invalid();
    }

    // Structural provenance only: the server still proves every historical byte against its committed bridge and original snapshot.
    private bool CaptureScopeAllowed(OutpostScopeMessage scope, HashSet<OutpostScopeMessage> sourceScopes) => scope.Equals(Metadata.Scope)
        || Metadata.RollbackBridge is { } bridge && SameNamespace(scope, bridge.SourceScope)
            && scope.SaveWriteEpoch <= bridge.SourceScope.SaveWriteEpoch
        || sourceScopes.Contains(scope);

    private HashSet<OutpostScopeMessage> ValidateSourceBridge()
    {
        int count = (Metadata.RollbackBridge is null ? 0 : 1) + (Metadata.RestoreBridge is null ? 0 : 1)
            + (Metadata.RollbackFromViewBridge is null ? 0 : 1);
        if (count > 1) throw Invalid();
        var scopes = new HashSet<OutpostScopeMessage>();
        OutpostSourceProvenance graph; OutpostAuthorizedSnapshotView root;
        if (Metadata.RestoreBridge is { } restore)
        { root = OutpostSourceGraph.ValidateRoot(restore); graph = restore.SourceProvenance; }
        else if (Metadata.RollbackFromViewBridge is { } rollback)
        { root = OutpostSourceGraph.ValidateRoot(rollback); graph = rollback.SourceProvenance; }
        else if (Metadata.RollbackBridge is { } actual && (actual.SourceProvenance is not null || actual.SourcePathDigest.Length != 0))
        { root = OutpostSourceGraph.ValidateRoot(actual); graph = actual.SourceProvenance ?? throw Invalid(); }
        else return scopes;
        if (!root.EffectiveScope.Equals(Metadata.Scope)) throw Invalid();
        var sources = graph.ActualSources.ToDictionary(s => s.Snapshot.SnapshotId, StringComparer.Ordinal);
        var views = graph.AuthorizedViews.ToDictionary(v => v.TransitionId, StringComparer.Ordinal);
        var seenSources = new HashSet<string>(StringComparer.Ordinal); var seenViews = new HashSet<string>(StringComparer.Ordinal);
        Visit(root);
        return scopes;

        void Visit(OutpostAuthorizedSnapshotView view)
        {
            if (!seenViews.Add(view.TransitionId)) return;
            scopes.Add(view.SourceScope); scopes.Add(view.EffectiveScope);
            if (view.Source.SourceCase == OutpostSourceReference.SourceOneofCase.AuthorizedView)
                Visit(views[view.Source.AuthorizedView.TransitionId]);
            else
            {
                var source = sources[view.Source.ActualSnapshotId];
                if (!seenSources.Add(source.Snapshot.SnapshotId)) return;
                scopes.Add(source.Snapshot.Scope);
                if (source.HistoricalAuthorization is { } historical) Visit(views[historical.TransitionId]);
            }
        }
    }

    private static bool SameNamespace(OutpostScopeMessage left, OutpostScopeMessage right) =>
        left.WorldInstanceId == right.WorldInstanceId && left.OwnerPlayerId == right.OwnerPlayerId
        && left.SaveGeneration == right.SaveGeneration && left.RestoreBoundary == right.RestoreBoundary;

    private void ValidateRollbackBridge()
    {
        if (Metadata.RollbackBridge is not { } bridge) return;
        ValidateScope(bridge.SourceScope); ValidateScope(bridge.CurrentScope);
        if (!Uuid(bridge.LifecycleOperationId) || !Uuid(bridge.TransitionId) || !Hash(bridge.TransitionDigest)
            || !Hash(bridge.LocalFrontierDigest) || !bridge.CurrentScope.Equals(Metadata.Scope)
            || !SameNamespace(bridge.SourceScope, bridge.CurrentScope) || bridge.SourceScope.SaveWriteEpoch >= bridge.CurrentScope.SaveWriteEpoch
            || bridge.CanonicalSnapshot is not { } proof || !Guid.TryParseExact(proof.SnapshotId, "N", out _)
            || !Hash(proof.RawSha256) || !bridge.SourceScope.Equals(proof.Scope) || bridge.MutableRegistry.Count > MaxEntries) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var original in bridge.MutableRegistry)
        {
            if (original.SchemaVersion != CurrentVersion || !Uuid(original.PawnCustodyId) || !OptionalUuid(original.AssignmentId)
                || !ids.Add(original.PawnCustodyId) || original.NonAuthoritativeMirror || original.Tombstone
                || !Text(original.PawnReferenceToken, 256) || !original.Scope.Equals(bridge.SourceScope)) throw Invalid();
        }
    }

    private void ValidateAdmissions()
    {
        if (Metadata.WorkerCaptures.Count > MaxEntries || Metadata.Caravans.Count > MaxEntries) throw Invalid();
        var workers = new HashSet<string>(StringComparer.Ordinal); var assignments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var worker in Metadata.WorkerCaptures)
        {
            if (!Uuid(worker.PawnCustodyId) || !Uuid(worker.AssignmentId) || !workers.Add(worker.PawnCustodyId) || !assignments.Add(worker.AssignmentId)
                || worker.Pawn?.Registry is not { } registry || registry.SchemaVersion != CurrentVersion || registry.PawnCustodyId != worker.PawnCustodyId
                || !Text(registry.PawnReferenceToken, 256) || !Hash(worker.BlobSha256) || worker.Pawn.ScribeXmlZstd.Length == 0
                || !SHA256.HashData(worker.Pawn.ToByteArray()).AsSpan().SequenceEqual(worker.BlobSha256.Span)
                || !float.IsFinite(worker.Consciousness) || !float.IsFinite(worker.Manipulation) || !float.IsFinite(worker.Moving)) throw Invalid();
            ValidateScope(registry.Scope);
        }
        var requests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var caravan in Metadata.Caravans)
        {
            if (!Text(caravan.CaravanId, 256) || !Text(caravan.SurfaceId, 128) || caravan.Tile < 0 || !Uuid(caravan.GameBranchId)
                || !Uuid(caravan.ClientRequestId) || !requests.Add(caravan.ClientRequestId) || !Hash(caravan.ManifestDigest)
                || caravan.RosterReferenceTokens.Count is < 1 or > 1024 || caravan.RemainingReferenceTokens.Count > 1024
                || caravan.SelectedCustodyIds.Count is < 1 or > 4 || caravan.SelectedCustodyIds.Any(id => !Uuid(id))
                || caravan.SelectedCustodyIds.Distinct(StringComparer.Ordinal).Count() != caravan.SelectedCustodyIds.Count
                || caravan.RosterReferenceTokens.Any(token => !Text(token, 256)) || caravan.RemainingReferenceTokens.Any(token => !Text(token, 256))
                || caravan.RosterReferenceTokens.Distinct(StringComparer.Ordinal).Count() != caravan.RosterReferenceTokens.Count
                || caravan.RemainingReferenceTokens.Distinct(StringComparer.Ordinal).Count() != caravan.RemainingReferenceTokens.Count
                || caravan.RemainingReferenceTokens.Any(token => !caravan.RosterReferenceTokens.Contains(token))) throw Invalid();
        }
    }
    private static bool Text(string value, int maximum) => value.Length is > 0 && value.Length <= maximum && !value.Any(char.IsControl);

    public void WriteSection(XmlWriter writer)
    {
        Validate();
        if (!Present) return;
        writer.WriteStartElement(SectionNode);
        writer.WriteElementString("version", "1");
        writer.WriteElementString("metadata", Convert.ToBase64String(Metadata.ToByteArray()));
        writer.WriteEndElement();
    }

    // RecoveryJson is durable evidence: serialize protobuf bytes, not its get-only collections.
    public sealed class JsonConverter : JsonConverter<OutpostRecoveryData>
    {
        public override OutpostRecoveryData Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || (reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > 6L * MaxBase64Chars)
                throw new JsonException("invalid outpost recovery JSON");
            string encoded = reader.GetString()!;
            if (encoded.Length > MaxBase64Chars) throw new JsonException("outpost recovery JSON exceeds limit");
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                if (bytes.Length > MaxMetadataBytes) throw new JsonException("outpost recovery JSON exceeds limit");
                using var input = CodedInputStream.CreateWithLimits(new MemoryStream(bytes, writable: false), MaxMetadataBytes, 32);
                var value = new OutpostRecoveryData { Metadata = OutpostSaveMetadata.Parser.ParseFrom(input) };
                value.Validate();
                return value;
            }
            catch (Exception ex) when (ex is FormatException or InvalidProtocolBufferException or InvalidDataException)
            { throw new JsonException("invalid outpost recovery JSON", ex); }
        }

        public override void Write(Utf8JsonWriter writer, OutpostRecoveryData value, JsonSerializerOptions options)
        {
            value.Validate();
            writer.WriteBase64StringValue(value.Metadata.ToByteArray());
        }
    }

    private static string ReadScalar(XmlReader reader, int maximum)
    {
        if (reader.IsEmptyElement) return "";
        var text = new StringBuilder();
        var buffer = new char[4096];
        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) return text.ToString();
            if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)) throw Invalid();
            if (!reader.CanReadValueChunk)
            {
                // XmlNodeReader (the game's already-loaded Scribe tree) exposes an existing value, not a chunk API.
                string value = reader.Value;
                if (value.Length > maximum - text.Length) throw Invalid();
                text.Append(value);
            }
            else
            {
                int count;
                while ((count = reader.ReadValueChunk(buffer, 0, buffer.Length)) != 0)
                {
                    if (text.Length > maximum - count) throw Invalid();
                    text.Append(buffer, 0, count);
                }
            }
        }
        throw Invalid();
    }
    private static void ValidateScope(OutpostScopeMessage? scope)
    {
        if (scope is null || scope.WorldInstanceId.Length is < 1 or > 128 || scope.RestoreBoundary.Length is < 1 or > 128
            || scope.OwnerPlayerId <= 0 || scope.SaveGeneration < 0 || scope.SaveWriteEpoch < 0
            || scope.WorldInstanceId.Any(char.IsControl) || scope.RestoreBoundary.Any(char.IsControl)) throw Invalid();
    }
    private static bool Uuid(string id) => (Guid.TryParseExact(id, "D", out var d) && id == d.ToString("D"))
        || (Guid.TryParseExact(id, "N", out var n) && id == n.ToString("N"));
    private static bool OptionalUuid(string id) => id.Length == 0 || Uuid(id);
    private static bool Hash(ByteString hash) => hash.Length == 32;
    private static InvalidDataException Invalid() => new("invalid outpost recovery metadata");
}
