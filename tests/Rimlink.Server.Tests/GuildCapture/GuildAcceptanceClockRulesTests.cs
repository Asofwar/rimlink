using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildAcceptanceClockRulesTests
{
    [Theory]
    [InlineData("valid")][InlineData("zero")][InlineData("negative")][InlineData("step-limit")][InlineData("total-budget")]
    [InlineData("active-battle")][InlineData("slot")][InlineData("revision")][InlineData("run")][InlineData("unreconciled")]
    public void Setup_clock_never_crosses_a_battle_slot_or_the_immutable_budget(string fault)
    {
        var now=new DateTime(2026,11,8,0,0,0,DateTimeKind.Utc);string run=Guid.NewGuid().ToString("D");
        var request=new GuildSetupClockRequest(new("world",Guid.NewGuid().ToString("D")),Guid.NewGuid().ToString("D"),new('a',64),new('b',64),run,
            Guid.NewGuid().ToString("D"),0,Guid.NewGuid().ToString("D"),new byte[32],GuildSetupClockPurpose.EligibilitySetup,now.AddDays(7).Ticks,[1,2],"");
        var projection=new GuildClockProjection(run,0,0,GuildClockBudget.V1,true);
        switch(fault)
        {
            case "zero":request=request with{TargetUtcTicks=now.Ticks};break;case "negative":request=request with{TargetUtcTicks=now.AddTicks(-1).Ticks};break;
            case "step-limit":request=request with{ExpectedRevision=32};projection=projection with{Revision=32};break;
            case "total-budget":projection=projection with{TotalAdvanceTicks=29*TimeSpan.TicksPerDay};break;
            case "revision":projection=projection with{Revision=1};break;case "run":projection=projection with{RunId=Guid.NewGuid().ToString("D")};break;
            case "unreconciled":projection=projection with{Ready=false};break;
        }
        request=GuildAcceptanceClockRules.Freeze(request);
        long Plan()=>GuildAcceptanceClockRules.Delta(request,projection,new(now,"process",10,TimeSpan.TicksPerSecond),fault=="active-battle",fault=="slot"?now.AddDays(1):null);
        if(fault=="valid")Assert.Equal(7*TimeSpan.TicksPerDay,Plan());else Assert.Throws<OutpostEvidenceUnavailableException>(()=>Plan());
    }
}
