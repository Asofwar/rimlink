namespace Rimlink.Client.GuildCapture;

// Main-thread lifecycle. A prefix permit is consumed by exactly one native tick postfix.
public sealed class BattleTickGate(BattleRules rules,TimeProvider clock)
{
    private bool started,withinTick,transportFailed,initialDefenderAi,defenderSeen;
    private long began,lastContact;
    private TimeSpan priorWallElapsed;
    private readonly long[] lastPresence = new long[2];
    public BattleRuleState State => rules.State;
    public TimeSpan EvaluatedWallElapsed { get; private set; }
    public TimeSpan EvaluatedTransportLostElapsed { get; private set; }
    public TimeSpan WallElapsed => started ? priorWallElapsed+clock.GetElapsedTime(began) : TimeSpan.Zero;
    public TimeSpan TransportLostElapsed => started ? clock.GetElapsedTime(lastContact) : TimeSpan.Zero;
    public bool Running => started && State.Outcome == BattleOutcome.Ongoing;

    public void Start(int nativeTick,bool defenderInitiallyAi=false,TimeSpan priorWallElapsed=default)
    {
        if (started || nativeTick != State.Tick || State.Outcome != BattleOutcome.Ongoing) throw new InvalidOperationException("battle Start does not match the unchanged initial tick");
        if(priorWallElapsed<TimeSpan.Zero || priorWallElapsed>TimeSpan.FromMinutes(20))throw new ArgumentOutOfRangeException(nameof(priorWallElapsed));
        this.priorWallElapsed=priorWallElapsed;
        began = lastContact = clock.GetTimestamp(); Array.Fill(lastPresence,began); initialDefenderAi=defenderInitiallyAi; started = true;
    }
    public void Contact(bool attackerPresent,bool defenderPresent)
    {
        if (!Running) return;
        Pump(); if (!Running) return; // a late reply cannot resurrect an expired match
        lastContact = clock.GetTimestamp(); transportFailed = false;
        if (attackerPresent) lastPresence[0] = lastContact;
        if (defenderPresent) { lastPresence[1] = lastContact; defenderSeen=true; }
    }
    public void TransportFailed() { if (Running) transportFailed = true; }
    public void Pump()
    {
        if (!Running) return;
        EvaluatedWallElapsed=WallElapsed; EvaluatedTransportLostElapsed=TransportLostElapsed;
        rules.CheckWallClock(EvaluatedWallElapsed);
        if (Running && EvaluatedTransportLostElapsed >= TimeSpan.FromSeconds(60)) rules.Abort(BattleEndReason.TransportLost);
    }
    public bool BeforeTick(int nativeTick)
    {
        if (withinTick) throw new InvalidOperationException("the previous native tick has no completion");
        if (!Running) return false;
        if (nativeTick != State.Tick) throw new InvalidOperationException("native simulation tick differs from the committed runtime");
        Pump();
        if (!Running || transportFailed || TransportLostElapsed >= TimeSpan.FromSeconds(1)) return false;
        withinTick = true; return true;
    }
    public BattleRuleState AfterTick(int nativeTick,IReadOnlyList<BattlePawn> pawns)
    {
        if (!withinTick || !Running) throw new InvalidOperationException("native tick has no prefix permit");
        EvaluatedWallElapsed=WallElapsed; EvaluatedTransportLostElapsed=TransportLostElapsed;
        var result = rules.Step(nativeTick,pawns,EvaluatedWallElapsed); withinTick = false; return result;
    }
    public bool UseAi(BattleSide side)
    {
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        return Running && (side==BattleSide.Defender && initialDefenderAi && !defenderSeen
            || clock.GetElapsedTime(lastPresence[(int)side-1]) >= TimeSpan.FromSeconds(30));
    }
}
