using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

/// <summary>First dispatch before local property is removed; recovery and replay need their own proof.</summary>
public static class OutpostFirstDispatch
{
    public static async Task<OutpostOperationReply> Send(TransferOutbox outbox, OutboxEntry entry,
        IMessage request, Func<Task<OutpostOperationReply>> send)
    {
        var kind = request switch
        {
            OutpostPrepareRequest => OutboxKind.OutpostPrepare,
            OutpostMutationRequest => OutboxKind.OutpostMutate,
            _ => throw new InvalidDataException("unsupported first dispatch"),
        };
        if (entry.Kind != kind || entry.Phase != OutboxPhase.Ready
            || entry.Outpost is not { ReplyUnknown: false } || !entry.Payload.AsSpan().SequenceEqual(request.ToByteArray())
            || !outbox.Entries.Any(e => ReferenceEquals(e, entry)))
            throw new InvalidDataException("first outpost dispatch requires its exact accepted, never-dispatched journal");
        outbox.MarkReplyUnknown(entry.Id);
        try { return await send(); }
        catch (RpcException error) when (RejectedBeforeTransition(kind, error))
        {
            // These replies prove the server declined before committing, and this caller has removed nothing locally.
            outbox.Remove(entry.Id);
            throw;
        }
    }

    private static bool RejectedBeforeTransition(OutboxKind kind, RpcException error)
    {
        if (error.StatusCode != StatusCode.FailedPrecondition) return false;
        var refusal = RpcErrorDetail.Read(error);
        var reason = (refusal.Code, refusal.Message);
        if (reason is (ErrorCode.OutpostIneligible, "worker-ineligible")
            or (ErrorCode.OutpostManifestMismatch, "manifest-not-confirmed" or "manifest-tile-ineligible")) return true;
        if (kind == OutboxKind.OutpostMutate)
            return reason is (ErrorCode.OutpostConflict, "mutation-outpost-changed" or "recall-worker-changed" or
                "switch-profile-invalid" or "switch-stock-not-empty" or "fraction-discard-confirmation-required");
        return reason is
            (ErrorCode.OutpostConflict, "outpost-slot-unavailable" or "outpost-staffing-unavailable")
            or (ErrorCode.TileTaken, "an outpost or settlement reserves this tile")
            or (ErrorCode.WalletInsufficient, "founding-reserve-unavailable")
            or (ErrorCode.OutpostManifestMismatch, "admission-manifest-changed")
            // Shared admission guards have dynamic reasons/amounts, but stable typed keys. These run after replay lookup.
            || (refusal.Code, refusal.Key) is
                (ErrorCode.EconomyFrozen, "Economy.Disabled" or "Economy.Paused")
                or (ErrorCode.PlayerFrozen, "Economy.PlayerFrozen")
                or (ErrorCode.InDebt, "Economy.InDebt")
                or (ErrorCode.UnderThreat, "Economy.UnderThreat");
    }
}
