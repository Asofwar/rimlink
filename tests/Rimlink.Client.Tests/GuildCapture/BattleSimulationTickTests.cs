using System.Buffers.Binary;
using Rimlink.Client.GuildCapture;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleSimulationTickTests
{
    private static BattlePawn[] Pawns()=>Enumerable.Range(0,8).Select(i=>new BattlePawn("fighter"+i,i<4?BattleSide.Attacker:BattleSide.Defender,
        100+i,200+i,true,i==1,i==2,i!=3,i/8f)).ToArray();
    [Fact]
    public void Fixed_roster_tick_payload_is_bounded_and_preserves_native_state_without_repeating_names()
    {
        var state=new BattleRuleState(580,29,BattleOutcome.Ongoing,BattleEndReason.None);var pawns=Pawns();byte[] bytes=BattleSimulationTick.Encode(state,pawns);
        Assert.Equal(83,bytes.Length);Assert.Equal(2,bytes[0]);Assert.Equal(state.Tick,BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1)));
        Assert.Equal(29,BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(5)));
        for(int i=0;i<8;i++)
        {
            var body=bytes.AsSpan(11+i*9,9);Assert.Equal(pawns[i].X,BinaryPrimitives.ReadInt16LittleEndian(body));
            Assert.Equal(pawns[i].Z,BinaryPrimitives.ReadInt16LittleEndian(body[2..]));Assert.Equal(pawns[i].Health,BinaryPrimitives.ReadSingleLittleEndian(body[4..]));
            Assert.Equal(pawns[i].Alive,(body[8]&1)!=0);Assert.Equal(pawns[i].Downed,(body[8]&2)!=0);
            Assert.Equal(pawns[i].Mental,(body[8]&4)!=0);Assert.Equal(pawns[i].CanFight,(body[8]&8)!=0);
        }
        // 30k native samples plus 48k bounded canonical command/ack pairs fit the 64MiB simulation artifact.
        Assert.True((83L+36)*30000+(1024L+36)*48000<67108864);
    }
    [Fact]
    public void Missing_or_unrepresentable_samples_are_refused_before_writing_a_journal_record()
    {
        var state=new BattleRuleState(0,0,BattleOutcome.Ongoing,BattleEndReason.None);var pawns=Pawns();
        Assert.Throws<InvalidDataException>(()=>BattleSimulationTick.Encode(state,pawns[..7]));
        pawns[0]=pawns[0] with{Health=float.NaN};Assert.Throws<InvalidDataException>(()=>BattleSimulationTick.Encode(state,pawns));
        pawns=Pawns();pawns[0]=pawns[0] with{X=40000};Assert.Throws<InvalidDataException>(()=>BattleSimulationTick.Encode(state,pawns));
    }
}
