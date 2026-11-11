using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

/// <summary>Canonical v1: RLOADMIN + uint32 LE version; fixed-order length-prefixed fields and explicit union/presence tags.</summary>
public static partial class OutpostAdminIntentCodec
{
    public const int MaximumCommandBytes = 64 * 1024;
    public const int MaximumReferences = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Encode(OutpostAdminIntent intent)
    {
        Validate(intent);
        using var w = new CanonicalWriter();
        w.Raw("RLOADMIN"u8); w.Number(intent.Version);
        WriteActor(w, intent.Actor); w.Text(intent.RequestId); w.Text(intent.Reason);
        WriteObservation(w, intent.Expected);
        var a = intent.Allocation;
        w.Text(a.ReceiptId); w.Text(a.TransitionId); w.Text(a.NextHeadId); w.Long(a.AuditId); w.Long(a.At.Ticks);
        w.Number((int)intent.Action);
        // This collection is a declared set. Never sort/rewrite the caller's array or identifiers.
        var references = intent.Evidence.OrderBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        w.Number(references.Length);
        foreach (var e in references) { w.Number((int)e.Kind); w.Text(e.Id); w.Optional(e.Digest); }
        switch (intent.Action)
        {
            case OutpostAdminActionKind.Initialize:
                var i = intent.Initialize!;
                w.Text(i.Installation); w.Text(i.Boundary); WriteSchema(w, i.Schema);
                break;
            case OutpostAdminActionKind.ActivateDomainFormat:
                var f = intent.Activation!;
                w.Number((int)f.From); w.Number((int)f.To); w.Blob(f.PreviousDomainDigest);
                WriteSchema(w, f.SourceSchema); WriteSchema(w, f.TargetSchema); w.Text(f.MigrationId);
                break;
            case OutpostAdminActionKind.ConfirmManifest:
                var m = intent.Manifest!;
                w.Text(m.ManifestId); w.Long(m.Revision); w.Blob(m.Digest); w.Blob(m.AffectedStateDigest);
                break;
            case OutpostAdminActionKind.Resolve:
                if (intent.Version == 2) { WriteRecovery(w, intent.Recovery!); break; }
                var r = intent.Resolution!;
                w.Number((int)r.Kind); w.Text(r.OperationId); w.Number((int)r.Original.Family);
                w.Text(r.Original.Kind); w.Text(r.Original.RequestId); w.OptionalText(r.Original.LocalOperationId);
                w.Blob(r.Original.IntentDigest); w.Present(r.Original.DerivedRoute is not null);
                if (r.Original.DerivedRoute is { } d)
                { w.Number((int)d.RequestKind); w.Text(d.ClientRequestId); w.Blob(d.RequestIntentSha256.Span); }
                WriteScope(w, r.Scope); w.Text(r.ExpectedState); w.Long(r.Revision);
                w.Present(r.CanonicalSnapshot is not null);
                if (r.CanonicalSnapshot is { } s) { w.Text(s.SnapshotId); w.Blob(s.RawSha256.Span); WriteScope(w, s.Scope); }
                break;
        }
        var bytes = w.Bytes();
        if (intent.Recovery?.Ordinary?.Input is { } input && (long)input.InputLength + bytes.Length > Rimlink.Protocol.Limits.MaxMessageBytes) throw Invalid();
        return bytes;
    }

    public static byte[] Digest(OutpostAdminIntent intent) => SHA256.HashData(Encode(intent));
    public static OutpostAdminIntent Freeze(OutpostAdminIntent intent) => Decode(Encode(intent));

    public static OutpostAdminIntent Decode(byte[] bytes)
    {
        if (bytes is null || bytes.Length > MaximumCommandBytes) throw Invalid();
        try
        {
            // No decoded field or caller-owned buffer is retained by reference.
            var r = new CanonicalReader((byte[])bytes.Clone());
            if (!r.Raw(8).SequenceEqual("RLOADMIN"u8)) throw Invalid();
            int version = r.Number(); if (version is not (1 or 2 or 3)) throw Invalid();
            var actor = new Actor(r.Long(), r.Long(), (Role)r.Number(), r.Text(256));
            string request = r.Text(36), reason = r.Text(1024);
            var observation = ReadObservation(r);
            var allocation = new OutpostAdminAllocation(r.Text(36), r.Text(36), r.Text(36), r.Long(), new(r.Long(), DateTimeKind.Utc));
            var action = (OutpostAdminActionKind)r.Number();
            if (!Enum.IsDefined(action)) throw Invalid();
            int count = r.Count(MaximumReferences);
            var evidence = new OutpostAdminEvidenceReference[count];
            for (int n = 0; n < count; n++) evidence[n] = new((OutpostAdminEvidenceKind)r.Number(), r.Text(1024), r.Optional(32));
            OutpostAdminInitializeSelection? initialize = null;
            OutpostAdminActivationSelection? activation = null;
            OutpostAdminManifestSelection? manifest = null;
            OutpostAdminResolutionSelection? resolution = null;
            OutpostAdminRecoveryTarget? recovery = null;
            switch (action)
            {
                case OutpostAdminActionKind.Initialize: initialize = new(r.Text(36), r.Text(36), ReadSchema(r)); break;
                case OutpostAdminActionKind.ActivateDomainFormat:
                    activation = new((OutpostDomainFormat)r.Number(), (OutpostDomainFormat)r.Number(), r.Blob(32), ReadSchema(r), ReadSchema(r), r.Text(128)); break;
                case OutpostAdminActionKind.ConfirmManifest: manifest = new(r.Text(256), r.Long(), r.Blob(32), r.Blob(32)); break;
                case OutpostAdminActionKind.Resolve:
                    if (version == 2) { recovery = ReadRecovery(r); break; }
                    var kind = (OutpostAdminResolutionKind)r.Number(); string operation = r.Text(256);
                    var family = (OutpostAdminOperationFamily)r.Number(); string originalKind = r.Text(64), originalRequest = r.Text(36);
                    string? localId = r.OptionalText(256); byte[] digest = r.Blob(32);
                    OrdinaryDerivedRoute? derived = r.Present()
                        ? new() { RequestKind = (OrdinaryRequestKind)r.Number(), ClientRequestId = r.Text(36), RequestIntentSha256 = ByteString.CopyFrom(r.Blob(32)) }
                        : null;
                    var scope = ReadScope(r); string state = r.Text(64); long revision = r.Long();
                    OutpostSnapshotProof? proof = r.Present()
                        ? new() { SnapshotId = r.Text(32), RawSha256 = ByteString.CopyFrom(r.Blob(32)), Scope = ReadScope(r) } : null;
                    resolution = new(kind, operation, new(family, originalKind, originalRequest, localId, digest, derived), scope, state, revision, proof);
                    break;
            }
            var intent = new OutpostAdminIntent(version, actor, request, reason, observation, allocation, action, evidence, initialize, activation, manifest, resolution, recovery);
            // Reject trailing fields, noncanonical set order, invalid text, empty hashes and unknown/ambiguous unions.
            if (!r.End || !Encode(intent).AsSpan().SequenceEqual(bytes)) throw Invalid();
            return intent;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { throw Invalid(error); }
    }

    public static byte[] ObservationDigest(OutpostAuthorityObservation observation)
    {
        ValidateObservation(observation);
        using var w = new CanonicalWriter();
        w.Raw("RLOADMOB"u8); w.Number(1); WriteObservation(w, observation);
        return SHA256.HashData(w.Bytes());
    }

    public static string ActorKey(Actor actor)
    {
        ValidateActor(actor);
        using var w = new CanonicalWriter();
        w.Raw("RLOADMAC"u8); w.Number(1); WriteActor(w, actor);
        return Convert.ToHexStringLower(SHA256.HashData(w.Bytes()));
    }

    private static void Validate(OutpostAdminIntent? i)
    {
        if (i is null || i.Version is not (1 or 2 or 3) || !Enum.IsDefined(i.Action)) throw Invalid();
        if (i.Version == 1 && i.Recovery is not null || i.Version == 2 && (i.Action != OutpostAdminActionKind.Resolve || i.Recovery is null || i.Resolution is not null)) throw Invalid();
        if (i.Version == 3 && (i.Action != OutpostAdminActionKind.ActivateDomainFormat || i.Recovery is not null)) throw Invalid();
        ValidateActor(i.Actor); Uuid(i.RequestId); Text(i.Reason, 1024);
        ValidateObservation(i.Expected);
        if (i.Allocation is not { AuditId: > 0 } a || a.At.Kind != DateTimeKind.Utc) throw Invalid();
        Uuid(a.ReceiptId); Uuid(a.TransitionId); Uuid(a.NextHeadId);
        if (new[] { a.ReceiptId, a.TransitionId, a.NextHeadId }.Distinct(StringComparer.Ordinal).Count() != 3) throw Invalid();
        if (i.Evidence is null || i.Evidence.Length > MaximumReferences) throw Invalid();
        var keys = new HashSet<(OutpostAdminEvidenceKind, string)>();
        foreach (var e in i.Evidence)
        {
            if (e is null || !Enum.IsDefined(e.Kind) || !keys.Add((e.Kind, e.Id))) throw Invalid();
            Text(e.Id, 1024); if (e.Digest is not null) Hash(e.Digest);
        }
        if ((i.Initialize is null ? 0 : 1) + (i.Activation is null ? 0 : 1) + (i.Manifest is null ? 0 : 1) + (i.Resolution is null ? 0 : 1) + (i.Recovery is null ? 0 : 1) != 1)
            throw Invalid();
        if (i.Action == OutpostAdminActionKind.Initialize)
        {
            if (i.Initialize is not { } init || i.Expected is not { Kind: OutpostAuthorityObservationKind.Uninitialized, Uninitialized: { } expected }) throw Invalid();
            Uuid(init.Installation); Uuid(init.Boundary); Schema(init.Schema);
            SameSchema(init.Schema, expected.Schema);
            return;
        }
        if (i.Expected is not { Kind: OutpostAuthorityObservationKind.Committed, Committed: { } committed }) throw Invalid();
        if (i.Version == 2)
        {
            if (committed.DomainFormat is not (OutpostDomainFormat.V2 or OutpostDomainFormat.V3)) throw Invalid();
            ValidateRecovery(i.Recovery!); return;
        }
        switch (i.Action)
        {
            case OutpostAdminActionKind.ActivateDomainFormat:
                var f = i.Activation ?? throw Invalid();
                bool guild = i.Version == 3;
                if (committed.DomainFormat != f.From || (guild
                    ? f.From != OutpostDomainFormat.V2 || f.To != OutpostDomainFormat.V3 || f.MigrationId != GuildCapture.GuildDomainDigest.MigrationId
                        || !OutpostDomainSchema.HasGuildTables(new(f.SourceSchema.ProfileId, ByteString.CopyFrom(f.SourceSchema.Digest), true))
                    : f.From != OutpostDomainFormat.V1 || f.To != OutpostDomainFormat.V2 || f.MigrationId != OutpostDomainSchema.AdminMigrationId)) throw Invalid();
                Hash(f.PreviousDomainDigest); Schema(f.SourceSchema); Schema(f.TargetSchema);
                SameSchema(f.SourceSchema, f.TargetSchema); SameSchema(f.SourceSchema, committed.Schema);
                break;
            case OutpostAdminActionKind.ConfirmManifest:
                if (i.Manifest is not { Revision: > 0 } m) throw Invalid();
                Text(m.ManifestId, 256); Hash(m.Digest); Hash(m.AffectedStateDigest);
                break;
            case OutpostAdminActionKind.Resolve:
                if (i.Resolution is not { Revision: >= 0, Original: { } original } selection || !Enum.IsDefined(selection.Kind)
                    || !Enum.IsDefined(original.Family)) throw Invalid();
                Text(selection.OperationId, 256); Text(original.Kind, 64);
                if (original.Family == OutpostAdminOperationFamily.Outpost)
                {
                    // The immutable source protocol already accepts both spellings. Preserve its exact bytes, never reformat a retained ID.
                    if (!OutpostAssignmentService.Uuid(original.RequestId) || Guid.Parse(original.RequestId) == Guid.Empty) throw Invalid();
                }
                else Uuid(original.RequestId);
                if (original.LocalOperationId is not null) Text(original.LocalOperationId, 256);
                Hash(original.IntentDigest); Scope(selection.Scope); Text(selection.ExpectedState, 64);
                if (original.DerivedRoute is { } derived)
                {
                    if (original.Family != OutpostAdminOperationFamily.Ordinary || !Enum.IsDefined(derived.RequestKind)
                        || derived.RequestKind == OrdinaryRequestKind.Unspecified) throw Invalid();
                    Uuid(derived.ClientRequestId); Hash(derived.RequestIntentSha256?.ToByteArray());
                }
                if (selection.Kind == OutpostAdminResolutionKind.OrdinaryReturn && original.Family != OutpostAdminOperationFamily.Ordinary
                    || selection.Kind is OutpostAdminResolutionKind.AbortNoRemoval or OutpostAdminResolutionKind.AbortWithReturn
                        && original.Family != OutpostAdminOperationFamily.Outpost) throw Invalid();
                if (selection.CanonicalSnapshot is { } snapshot)
                {
                    if (!Guid.TryParseExact(snapshot.SnapshotId, "N", out var id) || id == Guid.Empty) throw Invalid();
                    Hash(snapshot.RawSha256?.ToByteArray()); Scope(snapshot.Scope);
                }
                else if (selection.Kind != OutpostAdminResolutionKind.ExistingTerminal) throw Invalid();
                break;
            default: throw Invalid();
        }
    }

    private static void ValidateActor(Actor? actor)
    {
        if (actor is null || actor.AccountId < 0 || actor.PlayerId < 0 || actor.Role != Role.Admin) throw Invalid();
        Text(actor.Name, 256);
    }
    private static void ValidateObservation(OutpostAuthorityObservation? o)
    {
        if (o is null || !Enum.IsDefined(o.Kind) || (o.Committed is null ? 0 : 1) + (o.Uninitialized is null ? 0 : 1) + (o.Unavailable is null ? 0 : 1) != 1) throw Invalid();
        switch (o.Kind)
        {
            case OutpostAuthorityObservationKind.Committed:
                if (o.Committed is not { } c || !Enum.IsDefined(c.DomainFormat)) throw Invalid();
                Uuid(c.Installation); Text(c.World, 256); Uuid(c.Boundary); Uuid(c.HeadId); Hash(c.HeadDigest); Uuid(c.TransitionId); Schema(c.Schema);
                break;
            case OutpostAuthorityObservationKind.Uninitialized:
                if (o.Uninitialized is not { } u) throw Invalid();
                Text(u.World, 256); Hash(u.WorldBindingDigest); Hash(u.QualificationDigest); Schema(u.Schema);
                break;
            case OutpostAuthorityObservationKind.Unavailable:
                if (o.Unavailable is not { } unavailable) throw Invalid();
                Text(unavailable.Reason, 1024);
                break;
        }
    }
    private static void Scope(OutpostScopeMessage? scope)
    {
        if (scope is null || scope.OwnerPlayerId <= 0 || scope.SaveGeneration < 0 || scope.SaveWriteEpoch < 0) throw Invalid();
        Text(scope.WorldInstanceId, 256); Uuid(scope.RestoreBoundary);
    }
    private static void Schema(OutpostSchemaBinding? schema)
    { if (!OutpostDomainSchema.IsKnownBinding(schema, current: true)) throw Invalid(); }
    private static void SameSchema(OutpostSchemaBinding a, OutpostSchemaBinding b)
    { if (a.ProfileId != b.ProfileId || !a.Digest.AsSpan().SequenceEqual(b.Digest)) throw Invalid(); }
    private static void Hash(byte[]? bytes) { if (bytes is not { Length: 32 }) throw Invalid(); }
    private static void Uuid(string? value)
    { if (value?.Length != 36 || !Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw Invalid(); }
    private static void Text(string? value, int maximum)
    {
        if (value is null || value.Length > maximum || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw Invalid();
        try { if (Utf8.GetByteCount(value) > maximum) throw Invalid(); }
        catch (EncoderFallbackException e) { throw Invalid(e); }
    }

    private static void WriteActor(CanonicalWriter w, Actor a)
    { w.Long(a.AccountId); w.Long(a.PlayerId); w.Number((int)a.Role); w.Text(a.Name); }
    private static void WriteSchema(CanonicalWriter w, OutpostSchemaBinding s) { w.Text(s.ProfileId); w.Blob(s.Digest); }
    private static OutpostSchemaBinding ReadSchema(CanonicalReader r) => new(r.Text(128), r.Blob(32));
    private static void WriteObservation(CanonicalWriter w, OutpostAuthorityObservation o)
    {
        w.Number((int)o.Kind);
        switch (o.Kind)
        {
            case OutpostAuthorityObservationKind.Committed:
                var c = o.Committed!;
                w.Text(c.Installation); w.Text(c.World); w.Text(c.Boundary); w.Text(c.HeadId); w.Blob(c.HeadDigest); w.Text(c.TransitionId);
                w.Number((int)c.DomainFormat); WriteSchema(w, c.Schema); break;
            case OutpostAuthorityObservationKind.Uninitialized:
                var u = o.Uninitialized!; w.Text(u.World); w.Blob(u.WorldBindingDigest); w.Blob(u.QualificationDigest); WriteSchema(w, u.Schema); break;
            case OutpostAuthorityObservationKind.Unavailable: w.Text(o.Unavailable!.Reason); break;
        }
    }
    private static OutpostAuthorityObservation ReadObservation(CanonicalReader r) => (OutpostAuthorityObservationKind)r.Number() switch
    {
        OutpostAuthorityObservationKind.Committed => new(OutpostAuthorityObservationKind.Committed,
            Committed: new(r.Text(36), r.Text(256), r.Text(36), r.Text(36), r.Blob(32), r.Text(36), (OutpostDomainFormat)r.Number(), ReadSchema(r))),
        OutpostAuthorityObservationKind.Uninitialized => new(OutpostAuthorityObservationKind.Uninitialized,
            Uninitialized: new(r.Text(256), r.Blob(32), r.Blob(32), ReadSchema(r))),
        OutpostAuthorityObservationKind.Unavailable => new(OutpostAuthorityObservationKind.Unavailable, Unavailable: new(r.Text(1024))),
        _ => throw Invalid()
    };
    private static void WriteScope(CanonicalWriter w, OutpostScopeMessage s)
    { w.Text(s.WorldInstanceId); w.Long(s.OwnerPlayerId); w.Long(s.SaveGeneration); w.Long(s.SaveWriteEpoch); w.Text(s.RestoreBoundary); }
    private static OutpostScopeMessage ReadScope(CanonicalReader r) => new()
    { WorldInstanceId = r.Text(256), OwnerPlayerId = r.Long(), SaveGeneration = r.Long(), SaveWriteEpoch = r.Long(), RestoreBoundary = r.Text(36) };

    private sealed class CanonicalWriter(int maximum = MaximumCommandBytes) : IDisposable
    {
        private readonly MemoryStream stream = new();
        public void Raw(ReadOnlySpan<byte> bytes) { if (stream.Length + bytes.Length > maximum) throw Invalid(); stream.Write(bytes); }
        public void Number(int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); Raw(b); }
        public void Long(long value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, value); Raw(b); }
        public void Present(bool value) { Span<byte> b = stackalloc byte[1]; b[0] = value ? (byte)1 : (byte)0; Raw(b); }
        public void Blob(ReadOnlySpan<byte> value) { Number(value.Length); Raw(value); }
        public void Text(string value) => Blob(Utf8.GetBytes(value));
        public void Optional(byte[]? value) { Present(value is not null); if (value is not null) Blob(value); }
        public void OptionalText(string? value) { Present(value is not null); if (value is not null) Text(value); }
        public byte[] Bytes() => stream.ToArray();
        public void Dispose() => stream.Dispose();
    }
    private sealed class CanonicalReader(byte[] bytes)
    {
        private int offset;
        public bool End => offset == bytes.Length;
        public ReadOnlySpan<byte> Raw(int count)
        {
            if (count < 0 || count > bytes.Length - offset) throw Invalid();
            var result = bytes.AsSpan(offset, count); offset += count; return result;
        }
        public int Number() => BinaryPrimitives.ReadInt32LittleEndian(Raw(4));
        public long Long() => BinaryPrimitives.ReadInt64LittleEndian(Raw(8));
        public bool Present() => Raw(1)[0] switch { 0 => false, 1 => true, _ => throw Invalid() };
        public int Count(int maximum) { int count = Number(); if (count < 0 || count > maximum) throw Invalid(); return count; }
        public byte[] Blob(int maximum) => Raw(Count(maximum)).ToArray();
        public string Text(int maximum) { try { return Utf8.GetString(Raw(Count(maximum))); } catch (DecoderFallbackException e) { throw Invalid(e); } }
        public byte[]? Optional(int maximum) => Present() ? Blob(maximum) : null;
        public string? OptionalText(int maximum) => Present() ? Text(maximum) : null;
    }
    private static InvalidDataException Invalid(Exception? inner = null) => new("outpost-admin-intent-invalid", inner);
}
