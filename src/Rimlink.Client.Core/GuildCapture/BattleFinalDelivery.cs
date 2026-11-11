using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public static class BattleFinalDelivery
{
    public static async Task<BattleFinalReply> Deliver(BattleExecutorCheckpointStore store,BattleExecutorConnection connection,CancellationToken ct)
    {
        var envelope=store.ReadColdEnvelope();var state=store.Read()??throw Invalid();var assignment=state.Assignment;
        var final=BattleManifestContracts.ReadFinal(envelope.Manifest.Span,BattleManifestContracts.ReadPrepared(state.Start.Manifest.Span));
        foreach(var(kind,descriptor)in new[]{("final",final.FinalSnapshot),("simulation",final.SimulationJournal),("view",final.TerminalView)})
            await BattleArtifactDelivery.Upload(connection,assignment.Scope,kind,descriptor,store.ArtifactPath(kind),ct);
        var reply=await connection.Api.FinalAsync(new(){Executor=connection.Lease??throw Invalid(),Envelope=envelope},connection.Options(ct));var receipt=reply.Receipt;
        if(receipt is null || receipt.Version!=1 || receipt.WorldInstanceId!=assignment.Scope.WorldInstanceId || receipt.RestoreBoundary!=assignment.Scope.RestoreBoundary
            || receipt.BattleId!=assignment.Scope.BattleId || receipt.Attempt!=assignment.Scope.Attempt || receipt.Fence!=assignment.Scope.Fence || receipt.CampaignId!=assignment.CampaignId
            || receipt.ClientRequestId!=envelope.ClientRequestId || receipt.Outcome!=final.Outcome || receipt.Reason!=final.Reason || receipt.CommittedFinalDigest.Length!=32
            || !Uuid(receipt.TransitionId) || !Uuid(reply.AuthorityHeadId) || reply.AuthorityHeadDigest.Length!=32)throw Invalid();
        var current=store.Read()??throw Invalid();
        if(!current.Assignment.Equals(assignment) || !current.FinalEnvelopeSha256.Equals(state.FinalEnvelopeSha256))throw Invalid();
        if(current.Stage==BattleExecutorCheckpointStage.FinalCaptured)
        {current.Applied=receipt.Clone();current.Stage=BattleExecutorCheckpointStage.Applied;store.Save(current);}
        else if(!receipt.Equals(current.Applied))throw Invalid();
        return reply;
    }
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    private static InvalidDataException Invalid()=>new("battle-final-delivery-invalid");
}
