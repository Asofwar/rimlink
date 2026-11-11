using System.Numerics;
using Rimlink.Server.Application.GuildCapture;

namespace Rimlink.GuildAcceptanceHost;

internal sealed record GuildStandClockOrigin(int Version,DateTime DomainUtc,DateTime RealUtc,long InitialAdvanceTicks,GuildClockBudget Budget);

// Injected only into OutpostClock; system/auth/transport/native clocks and timers remain real.
internal sealed class GuildStandBusinessClock:TimeProvider,IGuildAcceptanceClockControl
{
    private readonly TimeProvider real;
    private readonly long origin,frequency;
    private readonly object gate=new();
    private readonly Dictionary<long,string> applied=[];
    private readonly string stand,bootstrap,source;
    private long offset,revision;
    private bool ready;
    internal string? Fault{get;private set;}
    internal string RunId{get;}=Guid.NewGuid().ToString("D");
    internal DateTime StartedRealUtc{get;}
    internal long StartedRealTimestamp=>origin;
    internal long RealFrequency=>frequency;
    internal GuildStandBusinessClock(TimeProvider real,string stand,string bootstrap,string source)
    {
        this.real=real;this.stand=stand;this.bootstrap=bootstrap;this.source=source;
        origin=real.GetTimestamp();frequency=real.TimestampFrequency;StartedRealUtc=real.GetUtcNow().UtcDateTime;
        if(frequency<=0)throw GuildAcceptanceClockRules.Invalid();
    }
    public GuildClockProjection ClockProjection{get{lock(gate)return new(RunId,revision,offset,GuildClockBudget.V1,ready&&Fault is null);}}
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public override long GetTimestamp()
    {
        lock(gate)
        {
            if(real.TimestampFrequency!=frequency)throw GuildAcceptanceClockRules.Invalid();
            BigInteger delta=(BigInteger)real.GetTimestamp()-origin;if(delta<0)throw GuildAcceptanceClockRules.Invalid();
            return checked((long)(delta*TimeSpan.TicksPerSecond/frequency)+offset);
        }
    }
    public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(StartedRealUtc).AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)=>real.CreateTimer(callback,state,dueTime,period);
    public void PublishClock(GuildClockStepReceipt receipt)
    {
        lock(gate)
        {
            try
            {
                if(!ready || Fault is not null)throw GuildAcceptanceClockRules.Invalid();Apply(receipt,recovery:false);
            }
            catch(Exception error){ready=false;Fault=error.Message;throw;}
        }
    }
    internal void Synchronize(IReadOnlyList<GuildClockStepReceipt> verifiedPrefix)
    {
        lock(gate)
        {
            try
            {
                if(ready || revision!=0 || offset!=0 || Fault is not null)throw GuildAcceptanceClockRules.Invalid();
                foreach(var receipt in verifiedPrefix)Apply(receipt,recovery:true);ready=true;
            }
            catch(Exception error){ready=false;Fault=error.Message;throw;}
        }
    }
    private void Apply(GuildClockStepReceipt receipt,bool recovery)
    {
        string digest=GuildAcceptanceClockRules.Digest(receipt);
        if(applied.TryGetValue(receipt.Revision,out var prior)){if(prior!=digest)throw GuildAcceptanceClockRules.Invalid();return;}
        if(receipt.Revision!=revision+1 || receipt.Revision>32 || receipt.Budget!=GuildClockBudget.V1 || receipt.Request.StandId!=stand
            || receipt.Request.BootstrapDigest!=bootstrap || receipt.Request.SourceIdentity!=source || !recovery&&receipt.Request.RunId!=RunId
            || receipt.DeltaTicks is <=0 or >7*TimeSpan.TicksPerDay || receipt.TotalAdvanceTicks!=checked(offset+receipt.DeltaTicks)
            || receipt.TotalAdvanceTicks>GuildClockBudget.V1.MaximumAdvanceTicks)throw GuildAcceptanceClockRules.Invalid();
        offset=receipt.TotalAdvanceTicks;revision=receipt.Revision;applied.Add(revision,digest);
    }
}
