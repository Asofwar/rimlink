using Rimlink.Client.GuildCapture;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleTickGateTests
{
    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long GetTimestamp() => Now;
        public override long TimestampFrequency => 1000;
    }
    private static BattlePawn[] Pawns() => Enumerable.Range(0,8).Select(i => new BattlePawn("p"+i,
        i < 4 ? BattleSide.Attacker : BattleSide.Defender,i < 4 ? 15 : 30,15,true,false,false,true,1)).ToArray();
    private static BattleTickGate Gate(Clock clock) => new(new(new(10,10,20,20),Pawns().Select(p => new BattleFighter(p.Id,p.Side)).ToArray()),clock);

    [Fact]
    public void No_tick_before_start_or_after_the_exact_native_terminal_tick()
    {
        var clock = new Clock(); var gate = Gate(clock);
        Assert.False(gate.BeforeTick(0)); gate.Start(0);
        for (int tick=1; tick<=3000; tick++) { Assert.True(gate.BeforeTick(tick-1)); gate.AfterTick(tick,Pawns()); }
        Assert.Equal(BattleOutcome.AttackerWin,gate.State.Outcome);
        Assert.False(gate.BeforeTick(3000));
        Assert.Throws<InvalidOperationException>(() => gate.AfterTick(3001,Pawns()));
    }
    [Fact]
    public void Transport_failure_pauses_at_the_boundary_and_cannot_resume_after_grace()
    {
        var clock = new Clock(); var gate = Gate(clock); gate.Start(0);
        Assert.True(gate.BeforeTick(0)); gate.AfterTick(1,Pawns());
        gate.TransportFailed(); Assert.False(gate.BeforeTick(1));
        clock.Now = 59999; Assert.False(gate.BeforeTick(1)); Assert.Equal(BattleOutcome.Ongoing,gate.State.Outcome);
        clock.Now = 60000; gate.Pump(); Assert.Equal(BattleEndReason.TransportLost,gate.State.Reason);
        gate.Contact(true,true); Assert.False(gate.BeforeTick(1)); Assert.Equal(1,gate.State.Tick);
    }
    [Fact]
    public void A_fresh_reply_resumes_an_interrupted_tick_without_replaying_simulation()
    {
        var clock = new Clock(); var gate = Gate(clock); gate.Start(0); gate.TransportFailed(); clock.Now=5000;
        gate.Contact(true,true); Assert.True(gate.BeforeTick(0)); gate.AfterTick(1,Pawns());
        Assert.Equal(1,gate.State.HeldTicks);
        clock.Now=6000; Assert.False(gate.BeforeTick(1)); // a silent network cannot keep simulating indefinitely
        gate.Contact(true,true); Assert.True(gate.BeforeTick(1));
    }
    [Fact]
    public void Offline_AI_uses_monotonic_thirty_seconds_and_resets_only_on_authorized_presence()
    {
        var clock = new Clock(); var gate = Gate(clock); gate.Start(0);
        gate.Contact(false,true); clock.Now=29999; gate.Contact(false,true); Assert.False(gate.UseAi(BattleSide.Attacker));
        clock.Now=30000; gate.Contact(false,true); Assert.True(gate.UseAi(BattleSide.Attacker)); Assert.False(gate.UseAi(BattleSide.Defender));
        gate.Contact(true,false); Assert.False(gate.UseAi(BattleSide.Attacker)); clock.Now=60000; gate.Contact(true,false);
        Assert.True(gate.UseAi(BattleSide.Defender));
    }
    [Fact]
    public void An_absent_defender_uses_AI_from_the_first_tick_then_normal_thirty_second_reconnect_grace()
    {
        var clock=new Clock();var gate=Gate(clock);gate.Start(0,defenderInitiallyAi:true);
        Assert.True(gate.UseAi(BattleSide.Defender));Assert.False(gate.UseAi(BattleSide.Attacker));
        gate.Contact(true,true);Assert.False(gate.UseAi(BattleSide.Defender));
        clock.Now=29999;gate.Contact(true,false);Assert.False(gate.UseAi(BattleSide.Defender));
        clock.Now=30000;gate.Contact(true,false);Assert.True(gate.UseAi(BattleSide.Defender));
    }
    [Fact]
    public void Wall_limit_stops_even_when_paused_and_is_not_a_defender_win()
    {
        var clock = new Clock(); var gate = Gate(clock); gate.Start(0); gate.TransportFailed();
        for(int i=1;i<1200;i++) { clock.Now=i*1000; gate.Contact(true,true); }
        clock.Now=1200000; gate.Pump(); Assert.Equal(BattleEndReason.WallClockLimit,gate.State.Reason);
        Assert.Equal(BattleOutcome.TechnicalAbort,gate.State.Outcome); Assert.Equal(0,gate.State.Tick);
    }
    [Fact]
    public void Crossing_wall_limit_inside_a_native_tick_cannot_publish_a_late_gameplay_win()
    {
        var clock = new Clock(); var gate = Gate(clock); gate.Start(0);
        for(int tick=1;tick<3000;tick++) { clock.Now=tick*400; gate.Contact(true,true); Assert.True(gate.BeforeTick(tick-1)); gate.AfterTick(tick,Pawns()); }
        clock.Now=1199999; gate.Contact(true,true); Assert.True(gate.BeforeTick(2999));
        clock.Now=1200000; gate.AfterTick(3000,Pawns());
        Assert.Equal(3000,gate.State.Tick); Assert.Equal(BattleOutcome.TechnicalAbort,gate.State.Outcome);
        Assert.Equal(BattleEndReason.WallClockLimit,gate.State.Reason); Assert.False(gate.BeforeTick(3000));
    }
    [Fact]
    public void A_second_prefix_or_skipped_postfix_is_a_runtime_error_not_a_duplicate_tick()
    {
        var gate = Gate(new()); gate.Start(0); Assert.True(gate.BeforeTick(0));
        Assert.Throws<InvalidOperationException>(() => gate.BeforeTick(0));
        Assert.Throws<InvalidOperationException>(() => gate.AfterTick(2,Pawns()));
    }
    [Fact]
    public void Recovering_a_Start_reply_keeps_the_original_wall_deadline_instead_of_granting_another_twenty_minutes()
    {
        var clock=new Clock();var gate=Gate(clock);gate.Start(0,priorWallElapsed:TimeSpan.FromMilliseconds(1199999));
        Assert.True(gate.BeforeTick(0));clock.Now=1;gate.AfterTick(1,Pawns());
        Assert.Equal(BattleOutcome.TechnicalAbort,gate.State.Outcome);Assert.Equal(BattleEndReason.WallClockLimit,gate.State.Reason);
        Assert.Throws<ArgumentOutOfRangeException>(()=>Gate(new()).Start(0,priorWallElapsed:TimeSpan.FromTicks(-1)));
    }
}
