using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Outposts;

public static partial class OutpostAdminIntentCodec
{
    private static void WriteRecovery(CanonicalWriter w, OutpostAdminRecoveryTarget target)
    {
        w.Number((int)target.Kind);
        if (target.Kind == OutpostAdminRecoveryTargetKind.OrdinaryCapture)
        {
            var o = target.Ordinary!; w.Text(o.OperationId);
            var original = o.Original; w.Number((int)original.Family); w.Text(original.Kind); w.Text(original.RequestId);
            w.Text(original.LocalOperationId!); w.Blob(original.IntentDigest); w.Present(original.DerivedRoute is not null);
            if (original.DerivedRoute is { } d) { w.Number((int)d.RequestKind); w.Text(d.ClientRequestId); w.Blob(d.RequestIntentSha256.Span); }
            WriteScope(w, o.Scope); w.Text(o.ExpectedState); w.Long(o.Revision); w.OptionalText(o.ExistingReturnAttemptId);
            WriteProof(w, o.RemovalSnapshot); WriteProof(w, o.CanonicalSnapshot); w.Present(o.Input is not null);
            if (o.Input is { } i)
            {
                w.Number(i.Version); w.Number(i.InputLength); w.Blob(i.InputDigest); w.Number(i.OriginalLength); w.Blob(i.OriginalDigest);
                w.Number(i.AuthorizationLength); w.Blob(i.AuthorizationDigest);
            }
        }
        else
        {
            var c = target.Canonical!; w.Long(c.SaveVersionId); w.Text(c.SnapshotId); WriteScope(w, c.OriginalScope); WriteScope(w, c.ViewScope);
            w.Long(c.RawLength); w.Blob(c.RawDigest); w.Number((int)c.ViewKind);
            if (c.ViewKind == OutpostAdminSnapshotViewKind.AuthorizedView)
            { w.Text(c.ViewRootId!); w.Blob(c.ViewRootDigest!); w.Blob(c.ViewPathDigest!); }
        }
    }

    private static OutpostAdminRecoveryTarget ReadRecovery(CanonicalReader r)
    {
        var kind = (OutpostAdminRecoveryTargetKind)r.Number();
        if (kind == OutpostAdminRecoveryTargetKind.OrdinaryCapture)
        {
            string id = r.Text(32); var family = (OutpostAdminOperationFamily)r.Number();
            string name = r.Text(64), request = r.Text(32), local = r.Text(32); byte[] digest = r.Blob(32);
            OrdinaryDerivedRoute? derived = r.Present() ? new() { RequestKind = (OrdinaryRequestKind)r.Number(), ClientRequestId = r.Text(32),
                RequestIntentSha256 = ByteString.CopyFrom(r.Blob(32)) } : null;
            var scope = ReadScope(r); string state = r.Text(32); long revision = r.Long(); string? attempt = r.OptionalText(36);
            var removal = ReadProof(r); var canonical = ReadProof(r);
            var input = r.Present() ? new OutpostAdminOrdinaryInputCommitment(r.Number(), r.Number(), r.Blob(32), r.Number(), r.Blob(32), r.Number(), r.Blob(32)) : null;
            return new(kind, Ordinary: new(id, new(family, name, request, local, digest, derived), scope, state, revision, attempt, removal, canonical, input));
        }
        if (kind != OutpostAdminRecoveryTargetKind.CanonicalSnapshot) throw Invalid();
        long version = r.Long(); string snapshot = r.Text(32); var originalScope = ReadScope(r); var viewScope = ReadScope(r);
        long length = r.Long(); byte[] raw = r.Blob(32); var view = (OutpostAdminSnapshotViewKind)r.Number();
        string? root = null; byte[]? rootDigest = null, path = null;
        if (view == OutpostAdminSnapshotViewKind.AuthorizedView) { root = r.Text(36); rootDigest = r.Blob(32); path = r.Blob(32); }
        return new(kind, Canonical: new(version, snapshot, originalScope, viewScope, length, raw, view, root, rootDigest, path));
    }

    private static void ValidateRecovery(OutpostAdminRecoveryTarget target)
    {
        if (!Enum.IsDefined(target.Kind) || (target.Ordinary is null ? 0 : 1) + (target.Canonical is null ? 0 : 1) != 1) throw Invalid();
        if (target.Kind == OutpostAdminRecoveryTargetKind.OrdinaryCapture)
        {
            if (target.Ordinary is not { Original: { } original, Revision: 0 } o || target.Canonical is not null
                || original.Family != OutpostAdminOperationFamily.Ordinary || original.LocalOperationId != o.OperationId
                || !Enum.TryParse<OrdinaryRequestKind>(original.Kind, out var kind) || kind == OrdinaryRequestKind.Unspecified
                || !Enum.IsDefined(kind) || kind.ToString() != original.Kind) throw Invalid();
            OriginalN(o.OperationId); OriginalN(original.RequestId); Hash(original.IntentDigest); Scope(o.Scope);
            ValidateProof(o.RemovalSnapshot); ValidateProof(o.CanonicalSnapshot);
            if (!o.Scope.Equals(o.RemovalSnapshot.Scope) || !o.Scope.Equals(o.CanonicalSnapshot.Scope)) throw Invalid();
            if (original.DerivedRoute is { } d)
            {
                if (kind != OrdinaryRequestKind.LivePropose || d.RequestKind != OrdinaryRequestKind.RaidLaunch
                    || d.ClientRequestId != original.RequestId || d.RequestIntentSha256 is null
                    || d.RequestIntentSha256.Span.SequenceEqual(original.IntentDigest)) throw Invalid();
                Hash(d.RequestIntentSha256.ToByteArray());
            }
            if (o.ExpectedState is not ("Unknown" or "Applied" or "ReturnStarted" or "ReturnAccepted")) throw Invalid();
            bool retained = o.ExpectedState is "ReturnStarted" or "ReturnAccepted";
            if (retained != (o.ExistingReturnAttemptId is not null)) throw Invalid();
            if (o.ExistingReturnAttemptId is { } attempt) Uuid(attempt);
            // A future new authorization needs exact input commitments; retained/source outcomes do not invent an attempt.
            if ((o.ExpectedState == "Unknown") != (o.Input is not null)) throw Invalid();
            if (o.Input is { } i)
            {
                if (i.Version != 1 || i.InputLength is < 1 or >= Limits.MaxMessageBytes || i.OriginalLength is < 1 or > Limits.MaxMessageBytes
                    || i.OriginalLength >= i.InputLength || i.AuthorizationLength is < 1 or > Limits.MaxMessageBytes) throw Invalid();
                Hash(i.InputDigest); Hash(i.OriginalDigest); Hash(i.AuthorizationDigest);
            }
        }
        else
        {
            if (target.Canonical is not { SaveVersionId: > 0, RawLength: > 0 } c || target.Ordinary is not null
                || !Enum.IsDefined(c.ViewKind)) throw Invalid();
            SnapshotN(c.SnapshotId); Scope(c.OriginalScope); Scope(c.ViewScope); Hash(c.RawDigest);
            if (c.ViewKind == OutpostAdminSnapshotViewKind.DirectActual)
            {
                if (!c.OriginalScope.Equals(c.ViewScope) || c.ViewRootId is not null || c.ViewRootDigest is not null || c.ViewPathDigest is not null) throw Invalid();
            }
            else { Uuid(c.ViewRootId); Hash(c.ViewRootDigest); Hash(c.ViewPathDigest); }
        }
    }

    private static void WriteProof(CanonicalWriter w, OutpostSnapshotProof proof)
    { w.Text(proof.SnapshotId); w.Blob(proof.RawSha256.Span); WriteScope(w, proof.Scope); }
    private static OutpostSnapshotProof ReadProof(CanonicalReader r) => new()
    { SnapshotId = r.Text(32), RawSha256 = ByteString.CopyFrom(r.Blob(32)), Scope = ReadScope(r) };
    private static void ValidateProof(OutpostSnapshotProof? proof)
    { if (proof is null) throw Invalid(); SnapshotN(proof.SnapshotId); Hash(proof.RawSha256?.ToByteArray()); Scope(proof.Scope); }
    private static void SnapshotN(string? id)
    { if (!Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty || id != parsed.ToString("N")) throw Invalid(); }
    private static void OriginalN(string? id)
    { if (!Guid.TryParseExact(id, "N", out var parsed) || id != parsed.ToString("N")) throw Invalid(); }
}
