using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal sealed record OutpostAdminCanonicalBinding(int Version, string Format, string CommandId, byte[] CommandDigest,
    OutpostAdminBusinessSemantic Semantic, OutpostAdminCanonicalTarget Snapshot, OutpostAdminEvidenceReference[] Prerequisites,
    byte[] AdminResultSha256);

public sealed partial class OutpostRestoreEvidence
{
    internal async Task<OutpostAdminCanonicalTarget> DescribeAdminCanonicalAsync(SaveVersion version, OutpostScopeMessage viewScope,
        string? archive, CancellationToken ct)
    {
        var raw = files.ReadEvidence(version, archive); _ = ReadActualDownload(version, archive);
        var resolver = new SourceResolver(this, ct); var actual = await resolver.ActualAsync(version, archive);
        var reference = await resolver.EffectiveAsync(version, viewScope, archive); var graph = resolver.Graph();
        bool direct = reference.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId;
        return new(version.Id, actual.Snapshot.SnapshotId, actual.Snapshot.Scope.Clone(), viewScope.Clone(), raw.Length,
            SHA256.HashData(raw), direct ? OutpostAdminSnapshotViewKind.DirectActual : OutpostAdminSnapshotViewKind.AuthorizedView,
            direct ? null : reference.AuthorizedView.TransitionId, direct ? null : reference.AuthorizedView.TransitionDigest.ToByteArray(),
            direct ? null : SHA256.HashData(graph.ToByteArray()));
    }
}

public sealed partial class OutpostAuthority
{
    internal async Task ReadAdminCanonicalCurrentAsync(OutpostLease lease, OutpostAdminCanonicalTarget selected, CancellationToken ct)
    {
        lease.Require(this);
        var s = selected.ViewScope;
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        if (!lease.CoversOwner(s.OwnerPlayerId) || s.WorldInstanceId != head.WorldId || s.RestoreBoundary != head.RestoreBoundary
            || !await db.Players.AsNoTracking().AnyAsync(p => p.Id == s.OwnerPlayerId && p.SaveGeneration == s.SaveGeneration
                && p.SaveWriteEpoch == s.SaveWriteEpoch, ct)) throw AdminMismatch();
        var current = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == s.OwnerPlayerId && v.IsCurrent).Take(2).ToArrayAsync(ct);
        if (current.Length != 1 || current[0].Id != selected.SaveVersionId) throw AdminMismatch();
        var resolver = new OutpostRestoreEvidence(db, files);
        _ = await resolver.GetActiveDownloadBridgesHeldAsync(lease, s.OwnerPlayerId, current[0], ct);
        var actual = await resolver.DescribeAdminCanonicalAsync(current[0], s, null, ct);
        if (!OutpostRestoreEvidence.Same(selected, actual)) throw AdminMismatch();
    }
}

internal static partial class OutpostAdminResolveEvidence
{
    internal static OutpostAdminCanonicalBinding ReadCanonicalBinding(OutpostTransition raw)
    {
        if (raw.Kind != "AdminResolved" || raw.Intent.Length is < 1 or > OutpostAdminIntentCodec.MaximumCommandBytes) throw Invalid();
        var binding = OutpostRestoreEvidence.Decode<OutpostAdminCanonicalBinding>(raw.Intent);
        if (binding is not { Version: 7, Format: "AdminResolvedCanonical-v1", Semantic: OutpostAdminBusinessSemantic.NoNewBusinessEffect,
                Snapshot: not null, CommandDigest.Length: 32, AdminResultSha256.Length: 32 }
            || !Guid.TryParseExact(binding.CommandId, "D", out var id) || id == Guid.Empty
            || binding.Prerequisites is not { Length: <= OutpostAdminIntentCodec.MaximumReferences }) throw Invalid();
        return binding;
    }

    internal static OutpostAdminEvidenceReference[] CanonicalPrerequisites(OutpostTransition prior, OutpostAdminCanonicalTarget selected) =>
        new[] { new OutpostAdminEvidenceReference(OutpostAdminEvidenceKind.Transition, prior.Id, (byte[])prior.Digest.Clone()),
            new(OutpostAdminEvidenceKind.Snapshot, selected.SnapshotId, (byte[])selected.RawDigest.Clone()) }
            .Concat(selected.ViewKind == OutpostAdminSnapshotViewKind.DirectActual ? [] : new[] {
                new OutpostAdminEvidenceReference(OutpostAdminEvidenceKind.Transition, selected.ViewRootId!, (byte[])selected.ViewRootDigest!.Clone()) })
            .DistinctBy(e => (e.Kind, e.Id)).OrderBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();

    internal static byte[] CanonicalBinding(OutpostAdminIntent intent, OutpostAdminCommandRecord receipt) =>
        OutpostRestoreEvidence.Encode(new OutpostAdminCanonicalBinding(7, "AdminResolvedCanonical-v1", receipt.Id, (byte[])receipt.CommandDigest.Clone(),
            OutpostAdminBusinessSemantic.NoNewBusinessEffect, intent.Recovery!.Canonical!, intent.Evidence, SHA256.HashData(receipt.Result)));

    private static async Task<OutpostAdminSemanticView> ReadCanonicalSemanticAsync(RimlinkDbContext db, SaveFiles files,
        OutpostTransition raw, IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        var binding = ReadCanonicalBinding(raw);
        var receipt = await db.OutpostAdminCommands.AsNoTracking().SingleOrDefaultAsync(c => c.Id == binding.CommandId
            && c.Command.Length <= OutpostAdminIntentCodec.MaximumCommandBytes && c.Result.Length <= OutpostAdminIntentCodec.MaximumCommandBytes, ct);
        if (receipt is null || receipt.TransitionId != raw.Id || !binding.CommandDigest.AsSpan().SequenceEqual(receipt.CommandDigest)) throw Invalid();
        var intent = OutpostAdminIntentCodec.Decode(receipt.Command);
        if (intent.Version != 2 || intent.Action != OutpostAdminActionKind.Resolve
            || intent.Recovery is not { Kind: OutpostAdminRecoveryTargetKind.CanonicalSnapshot, Ordinary: null, Canonical: { } selected }) throw Invalid();
        var proof = new OutpostSnapshotProof { SnapshotId = selected.SnapshotId, Scope = selected.OriginalScope.Clone(), RawSha256 = ByteString.CopyFrom(selected.RawDigest) };
        var codec = new OutpostLifecycleEvidence(db, files); var link = await codec.RollbackLinkAsync(proof, ct);
        var located = await codec.ResolveSnapshotAsync(link, ct);
        var actual = await new OutpostRestoreEvidence(db, files).DescribeAdminCanonicalAsync(located.Version, selected.ViewScope, located.Archive, ct);
        // Archived locators reconstruct physical identity without the original DB row ID. It remains frozen in the command.
        if (!OutpostRestoreEvidence.Same(selected, actual with { SaveVersionId = selected.SaveVersionId })
            || !transitions.TryGetValue(raw.PredecessorId ?? "", out var prior)) throw Invalid();
        var refs = CanonicalPrerequisites(prior, selected);
        if (refs.Length != intent.Evidence.Length || !refs.Zip(intent.Evidence).All(p => p.First.Kind == p.Second.Kind
                && p.First.Id == p.Second.Id && (p.First.Digest ?? []).AsSpan().SequenceEqual(p.Second.Digest))
            || !CanonicalBinding(intent, receipt).AsSpan().SequenceEqual(raw.Intent)) throw Invalid();
        var result = OutpostRestoreEvidence.Decode<OutpostAdminDurableResult>(receipt.Result);
        if (result.Kind != OutpostAdminDurableResultKind.CanonicalDownloadRequired || !result.NoReplayAuthorization
            || result.OutpostStatus is not null || result.OrdinaryStatus is not null
            || result.References is not [{ Kind: OutpostAdminEvidenceKind.Snapshot } reference] || reference.Id != selected.SnapshotId
            || reference.Digest is null || !reference.Digest.AsSpan().SequenceEqual(selected.RawDigest)) throw Invalid();
        var required = new HashSet<string>([Guid.ParseExact(selected.SnapshotId, "N").ToString("D")], StringComparer.Ordinal);
        if (selected.ViewRootId is not null) required.Add(selected.ViewRootId);
        string? cursor = raw.PredecessorId; int remaining = transitions.Count;
        while (required.Count > 0 && cursor is not null && remaining-- > 0 && transitions.TryGetValue(cursor, out var ancestor))
        { required.Remove(cursor); cursor = ancestor.PredecessorId; }
        if (required.Count != 0) throw Invalid();
        return new(raw, OutpostAdminBusinessSemantic.NoNewBusinessEffect);
    }
}
