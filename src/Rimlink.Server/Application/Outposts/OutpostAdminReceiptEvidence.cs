using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Backup;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Outposts;

public enum OutpostAdminDurableResultKind { AuthorityInitialized = 1, DomainFormatActivated = 2, ManifestConfirmed = 3, OutpostOutcome = 4, OrdinaryOutcome = 5, CanonicalDownloadRequired = 6 }
public sealed record OutpostAdminDurableResult(int Version, string ReceiptId, OutpostAdminDurableResultKind Kind,
    OutpostOperationStatus? OutpostStatus, OrdinaryOperationStatus? OrdinaryStatus,
    OutpostAdminEvidenceReference[] References, bool NoReplayAuthorization);
internal sealed record OutpostAdminTransitionBinding(int Version, string CommandId, byte[] CommandDigest);
internal sealed record OutpostAdminInitialBinding(int Version, string CommandId, byte[] CommandDigest,
    string Installation, string Boundary, string World, OutpostDomainFormat DomainFormat, OutpostSchemaBinding Schema);
internal sealed record OutpostAdminAuditProjection(int Version, long Id, long ActorAccountId, string ActorName,
    string Action, long? TargetAccountId, string Details, DateTime At);

/// <summary>Query-only bidirectional command/transition/audit linkage; the caller separately verifies chain cryptography and domain effects.</summary>
internal static class OutpostAdminReceiptEvidence
{
    internal static (OutpostAdminCommandRecord Receipt, AdminAction Audit) Create(OutpostAdminIntent intent,
        OutpostAdminDurableResult result, long? targetAccountId = null)
    {
        var frozen = OutpostAdminIntentCodec.Freeze(intent);
        if (targetAccountId is <= 0) throw Invalid();
        ValidateResult(frozen, result);
        // Result/audit bytes use strict code-owned JSON, never the canonical command digest encoding.
        byte[] resultBytes = Encode(result);
        string action = "outposts." + frozen.Action;
        string details = $"request={frozen.RequestId}; receipt={frozen.Allocation.ReceiptId}; reason={frozen.Reason}";
        var audit = new AdminAction { Id = frozen.Allocation.AuditId, ActorAccountId = frozen.Actor.AccountId,
            ActorName = frozen.Actor.Name, Action = action, TargetAccountId = targetAccountId, Details = details, At = frozen.Allocation.At };
        var projection = new OutpostAdminAuditProjection(1, audit.Id, audit.ActorAccountId, audit.ActorName,
            audit.Action, audit.TargetAccountId, audit.Details, audit.At);
        byte[] command = OutpostAdminIntentCodec.Encode(frozen);
        return (new OutpostAdminCommandRecord
        {
            Id = frozen.Allocation.ReceiptId, ActorKey = OutpostAdminIntentCodec.ActorKey(frozen.Actor),
            Action = frozen.Action.ToString(), RequestId = frozen.RequestId, Reason = frozen.Reason,
            Command = command, CommandDigest = SHA256.HashData(command), TransitionId = frozen.Allocation.TransitionId,
            NextHeadId = frozen.Allocation.NextHeadId, Result = resultBytes, AuditId = audit.Id,
            AuditProjection = Encode(projection), CreatedAt = frozen.Allocation.At
        }, audit);
    }

    internal static byte[] Binding(OutpostAdminCommandRecord receipt)
    {
        if (!Uuid(receipt.Id) || receipt.CommandDigest.Length != 32
            || !SHA256.HashData(receipt.Command).AsSpan().SequenceEqual(receipt.CommandDigest)) throw Invalid();
        var intent = OutpostAdminIntentCodec.Decode(receipt.Command);
        return intent.Action == OutpostAdminActionKind.Initialize
            ? Encode(new OutpostAdminInitialBinding(2, receipt.Id, (byte[])receipt.CommandDigest.Clone(),
                intent.Initialize!.Installation, intent.Initialize.Boundary, intent.Expected.Uninitialized!.World,
                OutpostDomainFormat.V2, intent.Initialize.Schema))
            : Encode(new OutpostAdminTransitionBinding(2, receipt.Id, (byte[])receipt.CommandDigest.Clone()));
    }

    internal static string TransitionKind(OutpostAdminActionKind action) => action switch
    {
        OutpostAdminActionKind.Initialize => "Initialize", OutpostAdminActionKind.ActivateDomainFormat => "DomainFormatActivated",
        OutpostAdminActionKind.ConfirmManifest => "AdminManifestConfirmed", OutpostAdminActionKind.Resolve => "AdminResolved",
        _ => throw Invalid()
    };

    internal static async Task ValidateAsync(RimlinkDbContext db, IReadOnlyList<OutpostTransition> transitions,
        OutpostSchemaObservation schema, CancellationToken ct)
    {
        try
        {
            if (!OutpostDomainSchema.IsKnownBinding(new(schema.ProfileId, schema.Digest.ToByteArray()), current: schema.IsCurrent)) throw Invalid();
            var selected = new List<(OutpostTransition Transition, OutpostAdminTransitionBinding Binding)>();
            foreach (var transition in transitions)
                if (ReadBinding(transition) is { } binding) selected.Add((transition, binding));
            if (!schema.IsCurrent)
            {
                // Exact historical pre-admin absence is query-only, not an inferred domain format.
                if (schema.ProfileId is not ("rimlink-outposts-schema/v1-crlf" or "rimlink-outposts-schema/v1-lf") || selected.Count != 0) throw Invalid();
                return;
            }
            var ids = selected.Select(s => s.Binding.CommandId).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
                || await db.OutpostAdminCommands.CountAsync(ct) != ids.Length
                || await db.OutpostAdminCommands.AnyAsync(r => !ids.Contains(r.Id), ct)) throw Invalid();
            var audits = new HashSet<long>();
            foreach (var (transition, binding) in selected)
            {
                // Bound fields before materializing untrusted bytes, even if SQLite checks were bypassed externally.
                var row = await db.OutpostAdminCommands.AsNoTracking().SingleOrDefaultAsync(r => r.Id == binding.CommandId
                    && r.Command.Length <= OutpostAdminIntentCodec.MaximumCommandBytes
                    && r.Result.Length <= OutpostAdminIntentCodec.MaximumCommandBytes
                    && r.AuditProjection.Length <= OutpostAdminIntentCodec.MaximumCommandBytes
                    && r.CommandDigest.Length == 32 && r.ActorKey.Length == 64 && r.Action.Length <= 64
                    && r.RequestId.Length == 36 && r.Reason.Length <= 1024 && r.TransitionId.Length == 36 && r.NextHeadId.Length == 36, ct);
                if (row is null || !audits.Add(row.AuditId)) throw Invalid();
                var intent = OutpostAdminIntentCodec.Decode(row.Command);
                ValidateRow(row, transition, binding, intent);
                var result = Decode<OutpostAdminDurableResult>(row.Result);
                ValidateResult(intent, result);
                var audit = Decode<OutpostAdminAuditProjection>(row.AuditProjection);
                var expected = Create(intent, result, audit.TargetAccountId);
                if (!expected.Receipt.AuditProjection.AsSpan().SequenceEqual(row.AuditProjection)
                    || audit.Version != 1 || audit.Id != row.AuditId
                    || !await db.AdminActions.AsNoTracking().AnyAsync(a => a.Id == audit.Id
                        && a.ActorAccountId == audit.ActorAccountId && a.ActorName == audit.ActorName
                        && a.Action == audit.Action && a.TargetAccountId == audit.TargetAccountId
                        && a.Details == audit.Details && a.At == audit.At, ct)) throw Invalid();
            }
        }
        catch (InvalidDataException) { throw Invalid(); }
    }

    internal static OutpostAdminTransitionBinding? ReadBinding(OutpostTransition transition)
    {
        if (transition.Kind is not ("Initialize" or "DomainFormatActivated" or "AdminManifestConfirmed" or "AdminResolved")) return null;
        if (transition.Intent.Length is 0 or > OutpostAdminIntentCodec.MaximumCommandBytes) throw Invalid();
        try
        {
            if (transition.Kind == "Initialize")
            {
                using var document = JsonDocument.Parse(transition.Intent, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
                if (!document.RootElement.TryGetProperty("version", out _))
                {
                    var original = Decode<LegacyInitialize>(transition.Intent);
                    if (!Uuid(original.Installation) || !Uuid(original.Boundary) || original.World != transition.WorldId
                        || original.Boundary != transition.RestoreBoundary) throw Invalid();
                    return null;
                }
                var initial = ReadInitial(transition);
                return new(initial.Version, initial.CommandId, initial.CommandDigest);
            }
            if (transition.Kind == "AdminResolved")
            {
                using var document = JsonDocument.Parse(transition.Intent, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.TryGetProperty("version", out var canonicalVersion) && canonicalVersion.ValueKind == JsonValueKind.Number
                    && canonicalVersion.GetInt32() == 7)
                {
                    var resolved = OutpostAdminResolveEvidence.ReadCanonicalBinding(transition);
                    return new(resolved.Version, resolved.CommandId, resolved.CommandDigest);
                }
                if (document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.GetInt32() is 3 or 4 or 5 or 6)
                {
                    var resolved = OutpostAdminResolveEvidence.Read(transition);
                    return new(resolved.Version, resolved.CommandId, resolved.CommandDigest);
                }
            }
            // A legacy generic pointer proves structural command closure only. It is never a business semantic grant.
            var binding = Decode<OutpostAdminTransitionBinding>(transition.Intent);
            if (binding.Version != 2 || !Uuid(binding.CommandId) || binding.CommandDigest.Length != 32) throw Invalid();
            return binding;
        }
        catch (JsonException) { throw Invalid(); }
    }

    internal static OutpostAdminInitialBinding ReadInitial(OutpostTransition transition)
    {
        var initial = Decode<OutpostAdminInitialBinding>(transition.Intent);
        if (transition.Kind != "Initialize" || initial.Version != 2 || initial.DomainFormat != OutpostDomainFormat.V2
            || !Uuid(initial.CommandId) || initial.CommandDigest.Length != 32 || !Uuid(initial.Installation) || !Uuid(initial.Boundary)
            || initial.World != transition.WorldId || initial.Boundary != transition.RestoreBoundary
            || !OutpostDomainSchema.IsKnownBinding(initial.Schema, current: true)) throw Invalid();
        return initial;
    }

    private static void ValidateRow(OutpostAdminCommandRecord row, OutpostTransition t, OutpostAdminTransitionBinding binding, OutpostAdminIntent intent)
    {
        if (intent.Action == OutpostAdminActionKind.Initialize)
        {
            var initial = ReadInitial(t);
            if (initial.Installation != intent.Initialize!.Installation || initial.Boundary != intent.Initialize.Boundary
                || initial.World != intent.Expected.Uninitialized!.World || initial.Schema.ProfileId != intent.Initialize.Schema.ProfileId
                || !initial.Schema.Digest.AsSpan().SequenceEqual(intent.Initialize.Schema.Digest)) throw Invalid();
        }
        var allocation = intent.Allocation;
        var committed = intent.Expected.Committed;
        string world = committed?.World ?? intent.Expected.Uninitialized!.World;
        string boundary = committed?.Boundary ?? intent.Initialize!.Boundary;
        var scope = intent.Resolution?.Scope ?? intent.Recovery?.Ordinary?.Scope ?? intent.Recovery?.Canonical?.ViewScope;
        if (row.Id != allocation.ReceiptId || row.Id != binding.CommandId || row.TransitionId != allocation.TransitionId
            || row.NextHeadId != allocation.NextHeadId || row.AuditId != allocation.AuditId
            || row.CreatedAt.Ticks != allocation.At.Ticks || row.CreatedAt.Kind == DateTimeKind.Local
            || row.ActorKey != OutpostAdminIntentCodec.ActorKey(intent.Actor) || row.Action != intent.Action.ToString()
            || row.RequestId != intent.RequestId || row.Reason != intent.Reason
            || !SHA256.HashData(row.Command).AsSpan().SequenceEqual(row.CommandDigest)
            || !row.CommandDigest.AsSpan().SequenceEqual(binding.CommandDigest)
            || t.Id != row.TransitionId || t.NextHeadId != row.NextHeadId || t.Kind != TransitionKind(intent.Action)
            || t.Actor != row.ActorKey || t.WorldId != world || t.RestoreBoundary != boundary
            || t.OldHeadId != (committed?.HeadId ?? Guid.Empty.ToString("D")) || t.PredecessorId != committed?.TransitionId
            || t.CreatedAt.Ticks != allocation.At.Ticks || t.CreatedAt.Kind == DateTimeKind.Local
            || t.PlayerId != (scope?.OwnerPlayerId ?? 0)
            || t.SaveGeneration != (scope?.SaveGeneration ?? 0)) throw Invalid();
    }

    private static void ValidateResult(OutpostAdminIntent intent, OutpostAdminDurableResult? result)
    {
        if (result is not { Version: 1, NoReplayAuthorization: true } || result.ReceiptId != intent.Allocation.ReceiptId
            || !Enum.IsDefined(result.Kind) || result.References is null || result.References.Length > OutpostAdminIntentCodec.MaximumReferences) throw Invalid();
        var ids = new HashSet<(OutpostAdminEvidenceKind, string)>();
        foreach (var reference in result.References)
            if (reference is null || !Enum.IsDefined(reference.Kind) || string.IsNullOrWhiteSpace(reference.Id)
                || reference.Id.Length > 1024 || reference.Id.Any(char.IsControl) || Encoding.UTF8.GetByteCount(reference.Id) > 1024
                || reference.Digest is not { Length: 32 } || !ids.Add((reference.Kind, reference.Id))) throw Invalid();
        bool noStatus = result.OutpostStatus is null && result.OrdinaryStatus is null;
        bool valid = intent.Action switch
        {
            OutpostAdminActionKind.Initialize => result.Kind == OutpostAdminDurableResultKind.AuthorityInitialized && noStatus && result.References.Length == 0,
            OutpostAdminActionKind.ActivateDomainFormat => result.Kind == OutpostAdminDurableResultKind.DomainFormatActivated && noStatus && result.References.Length == 0,
            OutpostAdminActionKind.ConfirmManifest => result.Kind == OutpostAdminDurableResultKind.ManifestConfirmed && noStatus
                && result.References is [var manifest] && manifest.Kind == OutpostAdminEvidenceKind.Manifest
                && manifest.Id == intent.Manifest!.ManifestId && manifest.Digest!.AsSpan().SequenceEqual(intent.Manifest.Digest),
            OutpostAdminActionKind.Resolve when intent.Version == 2 && intent.Recovery?.Ordinary is { Input: not null, ExpectedState: "Unknown" } ordinary =>
                result.Kind == OutpostAdminDurableResultKind.OrdinaryOutcome && result.OutpostStatus is null && result.OrdinaryStatus == OrdinaryOperationStatus.ReturnStarted
                && result.References is [var authorization] && authorization.Kind == OutpostAdminEvidenceKind.Operation
                && authorization.Id == intent.Allocation.TransitionId && authorization.Digest!.AsSpan().SequenceEqual(ordinary.Input.AuthorizationDigest),
            OutpostAdminActionKind.Resolve when intent.Version == 2 && intent.Recovery?.Ordinary is { Input: null, ExpectedState: "ReturnStarted" or "ReturnAccepted" } retained =>
                result.Kind == OutpostAdminDurableResultKind.OrdinaryOutcome && result.OutpostStatus is null
                && result.OrdinaryStatus == (retained.ExpectedState == "ReturnStarted" ? OrdinaryOperationStatus.ReturnStarted : OrdinaryOperationStatus.ReturnAccepted)
                && result.References is [var reference] && reference.Kind == OutpostAdminEvidenceKind.Operation && reference.Id == retained.ExistingReturnAttemptId
                && intent.Evidence.Any(e => e.Kind == reference.Kind && e.Id == reference.Id && e.Digest is not null
                    && e.Digest.AsSpan().SequenceEqual(reference.Digest)),
            OutpostAdminActionKind.Resolve when intent.Version == 2 && intent.Recovery?.Ordinary is { Input: null, ExpectedState: "Applied" } =>
                result.Kind == OutpostAdminDurableResultKind.OrdinaryOutcome && result.OutpostStatus is null && result.OrdinaryStatus == OrdinaryOperationStatus.Applied
                && result.References is [var positive] && positive.Kind == OutpostAdminEvidenceKind.Transition
                && intent.Evidence.Any(e => e.Kind == positive.Kind && e.Id == positive.Id && e.Digest is not null
                    && e.Digest.AsSpan().SequenceEqual(positive.Digest)),
            OutpostAdminActionKind.Resolve when intent.Version == 2 && intent.Recovery?.Canonical is { } canonical =>
                result.Kind == OutpostAdminDurableResultKind.CanonicalDownloadRequired && noStatus && result.References is [var selectedSnapshot]
                && selectedSnapshot.Kind == OutpostAdminEvidenceKind.Snapshot && selectedSnapshot.Id == canonical.SnapshotId
                && selectedSnapshot.Digest!.AsSpan().SequenceEqual(canonical.RawDigest),
            OutpostAdminActionKind.Resolve when intent.Version == 2 => false,
            OutpostAdminActionKind.Resolve when intent.Resolution!.Kind == OutpostAdminResolutionKind.CanonicalDownload =>
                result.Kind == OutpostAdminDurableResultKind.CanonicalDownloadRequired && noStatus && result.References is [var snapshot]
                && snapshot.Kind == OutpostAdminEvidenceKind.Snapshot && snapshot.Id == intent.Resolution.CanonicalSnapshot!.SnapshotId
                && snapshot.Digest!.AsSpan().SequenceEqual(intent.Resolution.CanonicalSnapshot.RawSha256.Span),
            OutpostAdminActionKind.Resolve when intent.Resolution!.Original.Family == OutpostAdminOperationFamily.Ordinary =>
                result.Kind == OutpostAdminDurableResultKind.OrdinaryOutcome && result.OutpostStatus is null
                && (result.OrdinaryStatus is OrdinaryOperationStatus.Applied or OrdinaryOperationStatus.ReturnAccepted
                    || intent.Resolution.Kind == OutpostAdminResolutionKind.OrdinaryReturn && result.OrdinaryStatus == OrdinaryOperationStatus.ReturnStarted),
            OutpostAdminActionKind.Resolve => result.Kind == OutpostAdminDurableResultKind.OutpostOutcome && result.OrdinaryStatus is null
                && (result.OutpostStatus == OutpostOperationStatus.Cancelled
                    || intent.Resolution!.Kind == OutpostAdminResolutionKind.ExistingTerminal && result.OutpostStatus == OutpostOperationStatus.Committed),
            _ => false
        };
        if (!valid) throw Invalid();
    }

    private static byte[] Encode<T>(T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, OutpostBackupManifest.Json);
        if (bytes.Length is 0 or > OutpostAdminIntentCodec.MaximumCommandBytes) throw Invalid();
        return bytes;
    }
    private static T Decode<T>(byte[] bytes)
    {
        if (bytes.Length is 0 or > OutpostAdminIntentCodec.MaximumCommandBytes) throw Invalid();
        try { return JsonSerializer.Deserialize<T>(bytes, OutpostBackupManifest.Json) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
    }
    private sealed record LegacyInitialize(string Installation, string Boundary, string World);
    private static bool Uuid(string? id) => id?.Length == 36 && Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty;
    private static OutpostEvidenceUnavailableException Invalid() => new("stored-admin-command-evidence-mismatch");
}
