using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostSnapshotGuard
{
    private async Task ValidateOrdinaryReturnsStoredAsync(Dictionary<string, OutpostTransition> transitions,
        List<OutpostSnapshotLink> links, IReadOnlyDictionary<string, OutpostAdminSemanticView> adminSemantics, CancellationToken ct)
    {
        var rows = await db.OutpostOrdinaryReturns.AsNoTracking().ToListAsync(ct);
        foreach (var transition in transitions.Values.Where(t => t.Kind == "OrdinaryReturnStarted"
            || adminSemantics.TryGetValue(t.Id, out var semantic) && semantic.BusinessKind == "OrdinaryReturnStarted"))
            if (!rows.Any(r => r.Id == transition.Id)) throw Unavailable("ordinary-return-projection-missing");
        foreach (var row in rows)
        {
            try
            {
                if (row.Intent.Length is < 1 or > Limits.MaxMessageBytes || row.Payload.Length > Limits.EconomyMaxParcelBytes)
                    throw Unavailable("ordinary-return-stored-size");
                using var input = CodedInputStream.CreateWithLimits(new MemoryStream(row.Intent, false), Limits.MaxMessageBytes, 32);
                var intent = OrdinaryCancelRequest.Parser.ParseFrom(input);
                var payload = OrdinaryReturnService.Payload(intent);
                var scope = intent.Scope; var capture = intent.Capture;
                if (row.Id != intent.ReturnAttemptId || row.WorldId != scope.WorldInstanceId || row.PlayerId != scope.OwnerPlayerId
                    || row.SaveGeneration != scope.SaveGeneration || row.SaveWriteEpoch != scope.SaveWriteEpoch || row.RestoreBoundary != scope.RestoreBoundary
                    || row.RequestKind != (int)capture.RequestKind || row.RequestId != capture.ClientRequestId || row.LocalOperationId != capture.LocalOperationId
                    || row.DerivedRequestKind != (int)(capture.DerivedRoute?.RequestKind ?? OrdinaryRequestKind.Unspecified)
                    || row.DerivedRequestId != (capture.DerivedRoute?.ClientRequestId ?? "")
                    || !payload.ToByteArray().AsSpan().SequenceEqual(row.Payload) || row.PayloadDigest.Length != 32
                    || !SHA256.HashData(row.Payload).AsSpan().SequenceEqual(row.PayloadDigest)
                    || !transitions.TryGetValue(row.Id, out var transition) || (adminSemantics.TryGetValue(row.Id, out var semantic)
                        ? semantic.BusinessKind : transition.Kind) != "OrdinaryReturnStarted"
                    || transition.WorldId != row.WorldId || transition.PlayerId != row.PlayerId || transition.SaveGeneration != row.SaveGeneration
                    || transition.RestoreBoundary != row.RestoreBoundary || !(adminSemantics.TryGetValue(row.Id, out var view) ? view.BusinessIntent ?? [] : transition.Intent).AsSpan().SequenceEqual(row.Intent))
                    throw Unavailable("ordinary-return-stored-intent");
                foreach (var proof in new[] { intent.RemovalSnapshot, intent.CanonicalSnapshot })
                {
                    if (proof?.Scope is null || !scope.Equals(proof.Scope) || proof.RawSha256.Length != 32)
                        throw Unavailable("ordinary-return-stored-scope");
                    var link = links.SingleOrDefault(l => l.WorldId == scope.WorldInstanceId && l.PlayerId == scope.OwnerPlayerId
                        && l.SaveGeneration == scope.SaveGeneration && l.SaveWriteEpoch == scope.SaveWriteEpoch && l.RestoreBoundary == scope.RestoreBoundary
                        && l.SnapshotId == proof.SnapshotId && l.RawSha256.AsSpan().SequenceEqual(proof.RawSha256.Span));
                    if (link is null) throw Unavailable("ordinary-return-stored-snapshot");
                    var (version, _) = await ResolveAsync(link, ct);
                    OrdinaryReturnService.RequireRemoval(SaveStore.RecoveryOf(version), intent);
                }
                // Also rechecked inside the staged SQLite write transaction, not just before BEGIN.
                if (await OrdinaryReturnService.SourceAsync(db, intent, ct) is not null)
                    throw Unavailable("ordinary-return-positive-source-conflict");
                if (row.State == "Started" && row.AcceptedSnapshotId is null)
                {
                    foreach (var item in capture.Pawns)
                    {
                        var envelope = item.Pawn.Registry;
                        var custody = await db.PawnCustody.AsNoTracking().SingleOrDefaultAsync(p => p.Id == envelope.PawnCustodyId, ct);
                        if (custody is null || custody.PlayerId != row.PlayerId || custody.WorldId != row.WorldId
                            || custody.SaveGeneration != row.SaveGeneration || custody.RestoreBoundary != row.RestoreBoundary
                            || (custody.AssignmentId ?? "") != envelope.AssignmentId || custody.LocationKind != "LocalReturnPending"
                            || custody.LocationId != row.Id || custody.FrontierTransitionId != row.Id || !custody.Tombstone)
                            throw Unavailable("ordinary-return-stored-custody");
                    }
                }
                else if (row.State == "Accepted" && row.AcceptedSnapshotId is not null)
                {
                    var accepted = links.SingleOrDefault(l => l.WorldId == row.WorldId && l.PlayerId == row.PlayerId
                        && l.SaveGeneration == row.SaveGeneration && l.SaveWriteEpoch == row.SaveWriteEpoch
                        && l.RestoreBoundary == row.RestoreBoundary && l.SnapshotId == row.AcceptedSnapshotId);
                    if (accepted is null) throw Unavailable("ordinary-return-accepted-snapshot-missing");
                    var metadata = ParseMetadata(accepted.Metadata);
                    var receipt = metadata.Receipts.SingleOrDefault(r => r.OrdinaryReturnAttemptId == row.Id);
                    if (receipt is null) throw Unavailable("ordinary-return-accepted-receipt-missing");
                    await ValidateOrdinaryReturnReceiptAsync(metadata, receipt, transition, ct);
                    var (version, _) = await ResolveAsync(accepted, ct);
                    RequireReturnedSnapshot(SaveStore.RecoveryOf(version), intent);
                }
                else throw Unavailable("ordinary-return-stored-state");
            }
            catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException or RpcException)
            { throw Unavailable("ordinary-return-stored-evidence-unavailable"); }
        }
    }
}
