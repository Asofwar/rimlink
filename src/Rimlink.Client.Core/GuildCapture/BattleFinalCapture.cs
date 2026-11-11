using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public sealed record BattleFinalCapture(BattleFinalManifestV1 Final,BattleTerminalViewV1 View,BattleFinalEnvelope Envelope)
{
    public static BattleFinalCapture Create(BattlePreparedManifestV1 start,BattleRuleState state,IReadOnlyList<BattlePawn> pawns,TimeSpan wall,TimeSpan transportLost,
        BattleDocumentDescriptor snapshot,BattleDocumentDescriptor journal,long viewSequence,string requestId)
    {
        if(pawns.Count!=8 || state.Outcome==BattleOutcome.Ongoing || wall<TimeSpan.Zero || transportLost<TimeSpan.Zero)throw new InvalidDataException("battle-final-capture-invalid");
        var final=new BattleFinalManifestV1{Version=1,WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,CampaignId=start.CampaignId,
            BattleId=start.BattleId,Attempt=start.Attempt,Fence=start.Fence,ExecutorId=start.ExecutorId,RuntimeInstanceId=start.RuntimeInstanceId,
            OriginalExecutorLeaseId=start.ExecutorLeaseId,OriginalExecutorLeaseFence=start.ExecutorLeaseFence,StartNonce=start.StartNonce,
            StartDigest=ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,start.ToByteArray())),ProfileDigest=start.ProfileDigest,
            InitialSnapshot=start.InitialSnapshot.Clone(),FinalSnapshot=snapshot.Clone(),SimulationJournal=journal.Clone(),TerminalTick=state.Tick,HeldTicks=state.HeldTicks,
            Outcome=state.Outcome switch{BattleOutcome.AttackerWin=>BattleTerminalOutcome.AttackerWin,BattleOutcome.DefenderWin=>BattleTerminalOutcome.DefenderWin,
                BattleOutcome.Draw=>BattleTerminalOutcome.Draw,BattleOutcome.TechnicalAbort=>BattleTerminalOutcome.TechnicalAbort,_=>throw new InvalidDataException("battle-final-capture-invalid")},
            Reason=(BattleTerminalReason)(int)state.Reason,WallElapsedMs=(long)wall.TotalMilliseconds,TransportLostElapsedMs=(long)transportLost.TotalMilliseconds,TerminalViewSequence=viewSequence};
        for(int i=0;i<8;i++)
        {
            var pawn=pawns[i];var entity=start.Entities[i];
            if(pawn.Id!=entity.FighterId || (int)pawn.Side!=(int)entity.Side)throw new InvalidDataException("battle-final-capture-invalid");
            final.Pawns.Add(new BattleTerminalPawn{FighterId=pawn.Id,EntityId=entity.EntityId,Side=entity.Side,X=pawn.X,Z=pawn.Z,
                Alive=pawn.Alive,Downed=pawn.Downed,Mental=pawn.Mental,CanFight=pawn.CanFight,Health=pawn.Health});
        }
        var view=new BattleTerminalViewV1{Version=1,WorldInstanceId=start.WorldInstanceId,RestoreBoundary=start.RestoreBoundary,BattleId=start.BattleId,
            Attempt=start.Attempt,Fence=start.Fence,StartDigest=final.StartDigest,Sequence=viewSequence,TerminalTick=state.Tick,HeldTicks=state.HeldTicks,
            Outcome=final.Outcome,Reason=final.Reason,Snapshot=snapshot.Clone()};view.Pawns.Add(final.Pawns.Select(p=>p.Clone()));
        byte[] viewBytes=view.ToByteArray();final.TerminalView=new(){Kind=BattleDocumentKind.TerminalView,SizeBytes=viewBytes.Length,Sha256=ByteString.CopyFrom(SHA256.HashData(viewBytes))};
        _=BattleManifestContracts.ReadFinal(final.ToByteArray(),start);_=BattleManifestContracts.ReadTerminalView(viewBytes,final);
        var envelope=new BattleFinalEnvelope{ClientRequestId=requestId,Manifest=ByteString.CopyFrom(final.ToByteArray())};
        _=BattleManifestContracts.ReadFinalEnvelope(envelope.ToByteArray());return new(final,view,envelope);
    }
}
