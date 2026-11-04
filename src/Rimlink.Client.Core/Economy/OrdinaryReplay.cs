using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
namespace Rimlink.Client.Economy;

public sealed partial class TransferOutbox
{
    private async Task<bool?> AttemptOrdinary(OutboxClients clients, OutboxEntry entry, LoginReply? authentication, CancellationToken ct)
    {
        var connection = clients.Economy.Connection;
        if (clients.Outposts is null || authentication is null || !ReferenceEquals(connection.Login, authentication)) return null;
        try
        {
            OrdinaryCancelRequest query;
            lock (gate)
            {
                var capture = ReadOrdinary(entry); var journal = entry.Ordinary!;
                if (authentication.OutpostVersion != 1 || authentication.Player?.PlayerId != capture.Scope.OwnerPlayerId
                    || authentication.SaveGeneration != capture.Scope.SaveGeneration || authentication.SaveWriteEpoch != capture.Scope.SaveWriteEpoch
                    || authentication.OutpostRestoreBoundary != capture.Scope.RestoreBoundary
                    || journal.AcceptedProof is null || journal.RemovalProof is null
                    || !journal.AcceptedProof.AsSpan().SequenceEqual(journal.RemovalProof)) return null;
                query = journal.ReturnRequest is not null ? ReadOrdinaryReturn(entry) : new OrdinaryCancelRequest
                {
                    Scope = capture.Scope.Clone(), Capture = capture, OriginalRequest = ByteString.CopyFrom(entry.Payload),
                    RemovalSnapshot = Parse(journal.AcceptedProof, OutpostSnapshotProof.Parser),
                    CanonicalSnapshot = Parse(journal.CanonicalProof ?? journal.AcceptedProof, OutpostSnapshotProof.Parser),
                    ReturnAttemptId = Guid.ParseExact(entry.Id, "N").ToString("D")
                };
                _ = OrdinaryReturnCodec.Payload(query);
            }
            var decision = await connection.ForAuthentication(authentication,
                () => clients.Outposts.OrdinaryOperation(new() { Operation = query }, ct));
            if (!ReferenceEquals(connection.Login, authentication)) return null;
            if (decision.Status == OrdinaryOperationStatus.Applied) return true;
            if (decision.Status is OrdinaryOperationStatus.ReturnStarted or OrdinaryOperationStatus.ReturnAccepted)
            {
                // A reply is not a local materialization receipt. The game layer alone can restore,
                // and only exact accepted Returned XML can remove this journal.
                if (entry.Ordinary!.ReturnRequest is not null) RecordOrdinaryReturnReply(entry.Id, query, decision);
                else HoldForRecovery(entry.Id, new Refusal(null, "ordinary return has no original local authorization", null, []));
                return null;
            }
            if (decision.Status != OrdinaryOperationStatus.Unknown || entry.Ordinary!.ReturnRequest is not null) return null;
            if (entry.Refusal is not null) return false;
            if (!ReadyFor(entry, authentication) || !CanReplay(clients, entry.Kind)) return null;
            return await Attempt(clients, entry, authentication, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RpcException error) when (EconomyClient.CodeOf(error) is ErrorCode.RequestIntentMismatch or ErrorCode.SaveWriteEpochMismatch or ErrorCode.RecoveryClientRequired)
        {
            HoldForRecovery(entry.Id, RpcErrorDetail.Read(error)); return null;
        }
        catch (Exception error)
        {
            log.Warn($"ordinary outbox decision {entry.Id} is unanswered; original bytes retained: {error.Message}");
            return null;
        }
    }
}
