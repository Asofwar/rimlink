using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal sealed record ManifestConfirmation(OutpostManifest Row, OutpostManifestDocument Document,
    OutpostRow[] Affected, Dictionary<string, OutpostManifest> Provenance, OutpostAdminManifestSelection Selection);

public sealed partial class OutpostManifestService
{
    // The caller owns the consistent read cut or sole authority transaction; neither helper acquires or commits one.
    internal async Task<ManifestConfirmation> PrepareAdminConfirmationAsync(string id, byte[] digest, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("manifest-confirmation-transaction-required");
        if (digest is not { Length: 32 }) throw new OutpostEvidenceUnavailableException("admin-intent-or-observation-mismatch");
        var row = await FindHeldAsync(id, Convert.ToHexStringLower(digest), ct);
        var document = OutpostManifestDocument.Parser.ParseFrom(row.Document);
        await ValidateHeldAsync(document, ct);
        if (row.Active && row.Confirmed || await db.OutpostManifests.AnyAsync(m => m.WorldId == row.WorldId && m.Active && m.Revision >= row.Revision, ct))
            throw new OutpostEvidenceUnavailableException("admin-manifest-not-eligible");
        var affected = await db.Outposts.AsNoTracking().Where(o => o.OccupiesSlot).OrderBy(o => o.Id).Take(10_001).ToArrayAsync(ct);
        var prepared = await db.OutpostOperations.AsNoTracking().Where(o => o.State == "Prepared" || o.State == "CancelFenced")
            .OrderBy(o => o.Id).Take(10_001).ToArrayAsync(ct);
        if (affected.Length > 10_000 || prepared.Length > 10_000) throw new OutpostEvidenceUnavailableException("admin-manifest-evidence-too-large");
        var ids = affected.Select(o => o.ManifestId).Distinct().ToArray();
        var provenance = await db.OutpostManifests.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        byte[] state = AffectedDigest(document.Binding, affected, prepared);
        return new(row, document, affected, provenance, new(row.Id, row.Revision, (byte[])row.Digest.Clone(), state));
    }

    internal async Task StageAdminConfirmationAsync(OutpostLease lease, OutpostAdminManifestSelection selected,
        string transitionId, DateTime at, CancellationToken ct)
    {
        lease.Require(authority);
        var current = await PrepareAdminConfirmationAsync(selected.ManifestId, selected.Digest, ct);
        if (selected.Revision != current.Selection.Revision || !selected.AffectedStateDigest.AsSpan().SequenceEqual(current.Selection.AffectedStateDigest)
            || current.Affected.Any(o => !lease.CoversOwner(o.PlayerId)))
            throw new OutpostEvidenceUnavailableException("admin-intent-or-observation-mismatch");
        await StageConfirmationCoreAsync(current.Row, current.Document, current.Affected, current.Provenance, transitionId, at, ct);
    }

    private async Task StageConfirmationCoreAsync(OutpostManifest row, OutpostManifestDocument document, IReadOnlyList<OutpostRow> affected,
        IReadOnlyDictionary<string, OutpostManifest> provenance, string transitionId, DateTime at, CancellationToken ct)
    {
        await db.OutpostManifests.Where(m => m.WorldId == row.WorldId && m.Active).ExecuteUpdateAsync(s => s.SetProperty(m => m.Active, false), ct);
        await db.OutpostManifests.Where(m => m.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Confirmed, true)
            .SetProperty(m => m.Active, true).SetProperty(m => m.ConfirmedAt, at), ct);
        foreach (var outpost in affected)
            if (outpost.WorldId != row.WorldId || !provenance.TryGetValue(outpost.ManifestId, out var source)
                || !outpost.ManifestDigest.AsSpan().SequenceEqual(source.Digest) || !Compatible(source, document, outpost))
                await db.Outposts.Where(o => o.Id == outpost.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.State, "SafetyHold")
                    .SetProperty(o => o.Revision, o => o.Revision + 1).SetProperty(o => o.LastTransitionId, transitionId), ct);
    }

    private static byte[] AffectedDigest(OutpostManifestBinding binding, OutpostRow[] outposts, OutpostOperation[] prepared)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        void Text(string value) { byte[] bytes = new UTF8Encoding(false, true).GetBytes(value); Blob(bytes); }
        void Blob(byte[] bytes)
        {
            if (bytes.Length > 32 * 1024 * 1024 || stream.Length + bytes.Length + 4 > 32 * 1024 * 1024)
                throw new OutpostEvidenceUnavailableException("admin-manifest-evidence-too-large");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        Text("Rimlink.Outposts.ManifestAffectedState/v1"); Blob(binding.ToByteArray()); writer.Write(outposts.Length);
        foreach (var o in outposts)
        {
            Text(o.Id); Text(o.WorldId); writer.Write(o.PlayerId); writer.Write(o.SaveGeneration); Text(o.RestoreBoundary);
            writer.Write(o.Tile); writer.Write(o.Profile); Text(o.State); writer.Write(o.OccupiesSlot); writer.Write(o.Revision); writer.Write(o.Stock);
            Text(o.FractionNumerator); Text(o.FractionDenominator); writer.Write(o.CheckpointUtc.Ticks); Text(o.ClockProcessId);
            writer.Write(o.ClockTimestamp); writer.Write(o.ClockFrequency); Text(o.ManifestId); Blob(o.ManifestDigest);
            writer.Write(o.LastTransitionId is not null); if (o.LastTransitionId is { } transition) Text(transition);
            writer.Write(o.CreatedAt.Ticks); writer.Write(o.ClosedAt is not null); if (o.ClosedAt is { } closed) writer.Write(closed.Ticks);
        }
        writer.Write(prepared.Length);
        foreach (var o in prepared)
        {
            Text(o.Id); Text(o.WorldId); writer.Write(o.PlayerId); writer.Write(o.SaveGeneration); writer.Write(o.SaveWriteEpoch);
            Text(o.RestoreBoundary); Text(o.RequestId); Text(o.Kind); Blob(o.IntentDigest); Text(o.State); writer.Write(o.ReservedSilver);
            writer.Write(o.OutpostId is not null); if (o.OutpostId is { } outpost) Text(outpost);
            writer.Write(o.TransitionId is not null); if (o.TransitionId is { } transition) Text(transition);
        }
        writer.Flush(); return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }
}
