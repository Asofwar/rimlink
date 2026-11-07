using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAuthority
{
    internal Task<OutpostAdminOrdinaryAppliedEvidence> ReadAdminOrdinaryAppliedAsync(OutpostAdminOrdinaryTarget selected, CancellationToken ct) =>
        OutpostAdminResolveEvidence.ReadAppliedAsync(db, files, selected, ct);

    internal Task<OutpostAdminOrdinaryReferenceEvidence> ReadAdminOrdinaryReferenceAsync(OutpostAdminOrdinaryTarget selected, bool historical, CancellationToken ct) =>
        OutpostAdminResolveEvidence.ReadOrdinaryReferenceAsync(db, files, selected, historical, ct);

    internal Task<OrdinaryCancelRequest> ReadNewAdminOrdinaryAsync(OutpostAdminOrdinaryTarget selected, OriginalOrdinaryInput input, string id, CancellationToken ct) =>
        OutpostAdminResolveEvidence.ReadNewOrdinaryAsync(db, files, selected, input, id, ct);

    internal Task<Storage.Entities.OutpostOrdinaryReturn> StageAdminOrdinaryAsync(OutpostLease lease, OutpostDomainTransaction domain,
        OutpostAdminIntent intent, OriginalOrdinaryInput input, CancellationToken ct) =>
        OutpostAdminResolveEvidence.StageOrdinaryHeldAsync(db, files, lease, domain, intent, input, ct);

    private async Task ValidateOrdinaryAdminCommandAsync(OutpostAdminIntent command, Storage.Entities.OutpostAuthority row, CancellationToken ct)
    {
        var selected = command.Recovery?.Ordinary;
        if (command.Version != 2 || command.Recovery?.Kind != OutpostAdminRecoveryTargetKind.OrdinaryCapture || selected is null
            || selected.Scope.WorldInstanceId != row.WorldId || selected.Scope.RestoreBoundary != row.RestoreBoundary
            || !await db.Players.AsNoTracking().AnyAsync(p => p.Id == selected.Scope.OwnerPlayerId && p.SaveGeneration == selected.Scope.SaveGeneration
                && p.SaveWriteEpoch == selected.Scope.SaveWriteEpoch, ct)) throw AdminMismatch();
        var captured = await OutpostAdminResolveEvidence.ReadOrdinaryTargetAsync(db, files, selected, ct);
        var tip = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == row.TransitionId, ct);
        var refs = selected.ExpectedState == "Applied"
            ? OutpostAdminResolveEvidence.AppliedPrerequisites(tip, selected,
                await OutpostAdminResolveEvidence.ReadAppliedAsync(db, files, selected, ct))
            : selected.Input is null
            ? OutpostAdminResolveEvidence.OrdinaryReferencePrerequisites(tip, selected,
                await OutpostAdminResolveEvidence.ReadOrdinaryReferenceAsync(db, files, selected, historical: false, ct))
            : OutpostAdminResolveEvidence.OrdinaryPrerequisites(tip, selected, captured);
        if (refs.Length != command.Evidence.Length || !refs.Zip(command.Evidence).All(pair => pair.First.Kind == pair.Second.Kind
            && pair.First.Id == pair.Second.Id && (pair.First.Digest ?? []).AsSpan().SequenceEqual(pair.Second.Digest))) throw AdminMismatch();
    }
}
