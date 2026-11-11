using System.Buffers.Binary;

namespace Rimlink.Client.GuildCapture;

// RLBGSJ01 body tag 2: fixed start-roster order; identity/side mapping is in the immutable Start manifest.
public static class BattleSimulationTick
{
    public static byte[] Encode(BattleRuleState state,IReadOnlyList<BattlePawn> pawns)
    {
        if(pawns.Count!=8 || state.Tick<0 || state.HeldTicks is <0 or >3000 || !Enum.IsDefined(state.Outcome) || !Enum.IsDefined(state.Reason))throw Invalid();
        byte[] bytes=new byte[83];bytes[0]=2;BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1),state.Tick);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5),state.HeldTicks);bytes[9]=(byte)state.Outcome;bytes[10]=(byte)state.Reason;
        for(int i=0;i<8;i++)
        {
            var pawn=pawns[i];
            if(pawn.X is <short.MinValue or >short.MaxValue || pawn.Z is <short.MinValue or >short.MaxValue || !float.IsFinite(pawn.Health) || pawn.Health is <0 or >1)throw Invalid();
            var body=bytes.AsSpan(11+i*9,9);BinaryPrimitives.WriteInt16LittleEndian(body,(short)pawn.X);BinaryPrimitives.WriteInt16LittleEndian(body[2..],(short)pawn.Z);
            BinaryPrimitives.WriteSingleLittleEndian(body[4..],pawn.Health);body[8]=(byte)((pawn.Alive?1:0)|(pawn.Downed?2:0)|(pawn.Mental?4:0)|(pawn.CanFight?8:0));
        }
        return bytes;
    }
    private static InvalidDataException Invalid()=>new("battle-simulation-sample-invalid");
}
