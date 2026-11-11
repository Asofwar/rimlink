using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static class BattleCommandContracts
{
    public const int MaximumBytes=16384;
    public static BattleCommandV1 Read(ReadOnlySpan<byte> bytes,BattlePreparedManifestV1 manifest,BattleFighterSide side)
    {
        if(bytes.Length is <1 or >MaximumBytes || side is not (BattleFighterSide.Attacker or BattleFighterSide.Defender))throw Invalid();
        BattleCommandV1 command;
        try { command=BattleCommandV1.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes.ToArray()); }
        catch(InvalidProtocolBufferException) { throw Invalid(); }
        if(command.Version!=1 || !command.ToByteArray().AsSpan().SequenceEqual(bytes)
            || !manifest.Entities.Any(e=>e.FighterId==command.FighterId && e.Side==side)) throw Invalid();
        switch(command.OrderCase)
        {
            case BattleCommandV1.OrderOneofCase.Move:
                if(command.Move.X<0 || command.Move.Z<0 || command.Move.X>=manifest.Arena.Width || command.Move.Z>=manifest.Arena.Height) throw Invalid();break;
            case BattleCommandV1.OrderOneofCase.AttackFighterId:
                if(!manifest.Entities.Any(e=>e.FighterId==command.AttackFighterId && e.Side!=side)) throw Invalid();break;
            case BattleCommandV1.OrderOneofCase.Draft:break;
            default:throw Invalid();
        }
        return command;
    }
    public static BattleCommandReceipt ReadReceipt(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is <1 or >512)throw Invalid();BattleCommandReceipt value;
        try{value=BattleCommandReceipt.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes.ToArray());}catch(InvalidProtocolBufferException){throw Invalid();}
        if(!value.ToByteArray().AsSpan().SequenceEqual(bytes) || value.Side is not(BattleFighterSide.Attacker or BattleFighterSide.Defender)
            || value.Sequence is <1 or >24000 || value.CommandDigest.Length!=32 || value.HasAcceptedTick && value.AcceptedTick is <0 or >30000)throw Invalid();
        bool valid=value.Disposition switch
        {
            BattleCommandDisposition.Executed=>value.HasAcceptedTick && value.Reason==BattleCommandReason.None,
            BattleCommandDisposition.Refused=>value.HasAcceptedTick && value.Reason is BattleCommandReason.TargetUnavailable or BattleCommandReason.InvalidState,
            BattleCommandDisposition.NotExecuted=>!value.HasAcceptedTick && value.Reason is BattleCommandReason.Terminal or BattleCommandReason.ControlRevoked,
            _=>false
        };
        return valid?value:throw Invalid();
    }
    private static InvalidDataException Invalid()=>new("battle-command-invalid");
}
