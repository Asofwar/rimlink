using OutpostAuthority=Rimlink.Server.Application.Outposts.OutpostAuthority;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.GuildAcceptanceHost;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildAcceptanceClockTests
{
    private sealed class Admission(GuildStandBusinessClock clock,GuildRuntimeGrant grant):IGuildRuntimeAdmission,IOutpostBuildQualification
    {
        public IGuildAcceptanceClockControl ClockControl=>clock;
        public GuildRuntimeGrant Observe()=>grant with{ClockSynchronized=clock.ClockProjection.Ready};
        OutpostBuildQualification IOutpostBuildQualification.Observe()=>new(OutpostRuntimeMode.IsolatedAcceptance,OutpostQualificationState.Pending,grant.SourceIdentity,true,true);
    }
    [Fact]
    public async Task Protected_clock_commit_replays_once_recovers_before_admission_and_detects_tampered_history()
    {
        using var f=await GuildFixture.Create(twoGuilds:true);
        string stand=Guid.NewGuid().ToString("D"),bootstrap=new('b',64),source=new('a',64);
        var clock=new GuildStandBusinessClock(f.Time,stand,bootstrap,source);clock.Synchronize([]);
        var grant=new GuildRuntimeGrant(stand,bootstrap,source,f.Scope.WorldId,f.Scope.Boundary,GuildAcceptanceScope.Home);
        var provider=new Admission(clock,grant);
        var feature=new OutpostFeature(Options.Create(new OutpostOptions()),provider,guildAdmission:provider);
        var authority=new OutpostAuthority(f.H.Db,f.H.Anchor,f.H.Files,f.H.Owners,feature);
        Assert.Equal(OutpostStartupState.Ready,(await authority.OpenAsync(default)).State);
        await authority.BindGuildAcceptanceAsync(default);
        var domain=new OutpostClock(clock);var service=f.ForAuthority(authority,domain);
        var head=await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var request=new GuildSetupClockRequest(f.Scope,stand,bootstrap,source,clock.RunId,Guid.NewGuid().ToString("D"),0,
            head.HeadId,head.HeadDigest,GuildSetupClockPurpose.EligibilitySetup,domain.Sample().Utc.AddDays(7).Ticks,[1,2],"");
        long realTimestamp=f.Time.GetTimestamp();
        var receipt=await service.AdvanceSetupClockAsync(Actor.Api,request,default);
        Assert.Equal(7*TimeSpan.TicksPerDay,clock.ClockProjection.TotalAdvanceTicks);
        Assert.Equal(realTimestamp,f.Time.GetTimestamp());
        Assert.Equal(GuildAcceptanceClockRules.Digest(receipt),GuildAcceptanceClockRules.Digest(await service.AdvanceSetupClockAsync(Actor.Api,request,default)));
        Assert.Equal(7*TimeSpan.TicksPerDay,clock.ClockProjection.TotalAdvanceTicks);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(()=>service.AdvanceSetupClockAsync(Actor.Api,request with{TargetUtcTicks=request.TargetUtcTicks+1},default));
        Assert.Single(await GuildAuthorityEvidence.ClockJournalAsync(f.H.Db,default));
        Assert.Equal(OutpostStartupState.Ready,(await authority.OpenAsync(default)).State);
        var restarted=new GuildStandBusinessClock(f.Time,stand,bootstrap,source);
        var recoveryProvider=new Admission(restarted,grant);
        var recoveryFeature=new OutpostFeature(Options.Create(new OutpostOptions()),recoveryProvider,guildAdmission:recoveryProvider);
        var recovered=new OutpostAuthority(f.H.Db,f.H.Anchor,f.H.Files,f.H.Owners,recoveryFeature);
        Assert.Equal(OutpostStartupState.Ready,(await recovered.OpenAsync(default)).State);
        Assert.Throws<OutpostEvidenceUnavailableException>(()=>recovered.RequireRuntimeGuild());
        restarted.Synchronize(await GuildAuthorityEvidence.ClockJournalAsync(f.H.Db,default));recovered.RequireRuntimeGuild();
        Assert.Equal(domain.Sample().Utc,new OutpostClock(restarted).Sample().Utc);
        restarted.PublishClock(receipt);
        Assert.Equal(7*TimeSpan.TicksPerDay,restarted.ClockProjection.TotalAdvanceTicks);
        var operation=await f.H.Db.Set<GuildCaptureOperation>().SingleAsync(o=>o.Kind==GuildAcceptanceClockRules.Kind);
        operation.Receipt=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(receipt with{TotalAdvanceTicks=1});
        await Assert.ThrowsAsync<DbUpdateException>(()=>f.H.Db.SaveChangesAsync());
        string trigger=await f.H.Db.Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_master WHERE name='guild_capture_operations_no_update'").SingleAsync();
        await f.H.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER guild_capture_operations_no_update");
        await f.H.Db.SaveChangesAsync();await f.H.Db.Database.ExecuteSqlRawAsync(trigger);
        Assert.Equal(OutpostStartupState.Maintenance,(await recovered.OpenAsync(default)).State);
    }

    [Fact]
    public void A_changed_replayed_clock_receipt_closes_the_projection()
    {
        var real=new Support.ManualTimeProvider();string stand=Guid.NewGuid().ToString("D"),bootstrap=new('b',64),source=new('a',64);
        var clock=new GuildStandBusinessClock(real,stand,bootstrap,source);clock.Synchronize([]);
        var before=new OutpostClock(clock).Sample();
        var request=new GuildSetupClockRequest(new("world",Guid.NewGuid().ToString("D")),stand,bootstrap,source,clock.RunId,
            Guid.NewGuid().ToString("D"),0,Guid.NewGuid().ToString("D"),new byte[32],GuildSetupClockPurpose.EligibilitySetup,before.Utc.AddDays(1).Ticks,[1],"");
        var receipt=new GuildClockStepReceipt(1,Actor.Api,request,1,TimeSpan.TicksPerDay,TimeSpan.TicksPerDay,"",before,
            before with{Utc=before.Utc.AddDays(1),Timestamp=before.Timestamp+TimeSpan.TicksPerDay},new(real.Now.UtcDateTime,real.Timestamp,real.TimestampFrequency),GuildClockBudget.V1,Guid.NewGuid().ToString("D"));
        clock.PublishClock(receipt);
        Assert.Throws<OutpostEvidenceUnavailableException>(()=>clock.PublishClock(receipt with{TransitionId=Guid.NewGuid().ToString("D")}));
        Assert.False(clock.ClockProjection.Ready);Assert.NotNull(clock.Fault);
    }
}
