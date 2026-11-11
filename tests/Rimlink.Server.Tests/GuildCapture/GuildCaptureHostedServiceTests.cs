using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;
using Xunit.Abstractions;
using OutpostAuthority=Rimlink.Server.Application.Outposts.OutpostAuthority;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildCaptureHostedServiceTests(ITestOutputHelper output)
{
    private sealed class TestLogger(ITestOutputHelper output):ILogger<GuildCaptureHostedService>
    {
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> format)
            =>output.WriteLine(format(state,error)+(error is null?"":Environment.NewLine+error));
    }
    private sealed class Admission(GuildRuntimeGrant grant):IGuildRuntimeAdmission,IOutpostBuildQualification
    {
        public GuildRuntimeGrant Observe()=>grant;
        OutpostBuildQualification IOutpostBuildQualification.Observe()=>new(OutpostRuntimeMode.IsolatedAcceptance,OutpostQualificationState.Pending,grant.SourceIdentity,true,true);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task A_qualified_HOME_host_prepares_due_campaigns_with_Product_disabled(bool publishingCut)
    {
        using var f=await GuildFixture.Create(twoGuilds:true);
        var provider=new Admission(new(Guid.NewGuid().ToString("D"),new('b',64),new('a',64),f.Scope.WorldId,f.Scope.Boundary,GuildAcceptanceScope.Home));
        var feature=new OutpostFeature(Options.Create(new OutpostOptions()),provider,guildAdmission:provider);
        var authority=new OutpostAuthority(f.H.Db,f.H.Anchor,f.H.Files,f.H.Owners,feature);
        await authority.OpenAsync(default);await authority.BindGuildAcceptanceAsync(default);
        var service=f.ForAuthority(authority,f.Clock);
        var approval=await service.ApproveBattleProfileAsync(Actor.Api,await GuildProfileApprovalTests.Stage(f),default);
        f.UseExecutorProfile(Convert.FromHexString(approval.ProfileDigest),BattleManifestContracts.ReadProfile(f.Profiles.ReadHeld(approval.Request.Profile)));
        service=f.ForAuthority(authority,f.Clock);
        var target=await service.PlaceAsync(Actor.Api,f.Scope,Guid.NewGuid().ToString("D"),0,GuildResource.Steel,"qualified HOME fixture",default);
        var consent=await service.PreviewConsentAsync(1,50,f.Scope,target.OutpostId,12,default);
        await service.AcceptConsentAsync(1,50,f.Scope,Guid.NewGuid().ToString("D"),target.OutpostId,12,consent.Digest,default);
        f.RegisterExecutor();
        var scheduled=await service.DeclareCampaignAsync(2,new(f.Scope,Guid.NewGuid().ToString("D"),60,target.OutpostId,1,2),default);
        f.Time.Advance(scheduled.ScheduledAtUtc-f.Time.Now.UtcDateTime);f.RegisterExecutor();

        var config=Options.Create(new GuildCaptureOptions());
        Assert.False(config.Value.Enabled);
        using var services=new ServiceCollection().AddSingleton(feature)
            .AddSingleton(new GuildCampaignScheduler(new(f.H.Data,f.H.Files,authority,feature,f.Battles),f.Clock,service))
            .AddSingleton(sp=>new GuildCaptureHostedService(sp.GetRequiredService<IServiceScopeFactory>(),config,new TestLogger(output)))
            .BuildServiceProvider();
        using var guarded=new OutpostDomainHostedService<GuildCaptureHostedService>(services,feature,NullLogger<OutpostDomainHostedService<GuildCaptureHostedService>>.Instance);
        await using var read=new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(f.H.Db.Database.GetConnectionString()).Options);
        if(publishingCut)
        {
            var published=feature.GuildCut;Task pending;
            using(await f.H.Files.PerStoreLock.HoldAsync(default))
            {
                feature.GuildCut=null;
                try
                {
                    pending=services.GetRequiredService<GuildCaptureHostedService>().TickAsync(default);
                    Assert.False(pending.IsCompleted); // Wait for the writer's S barrier instead of skipping the due pass.
                }
                finally {feature.GuildCut=published;}
            }
            await pending;
        }
        await guarded.StartAsync(default);
        try
        {
            var elapsed=Stopwatch.StartNew();
            while(elapsed.Elapsed<TimeSpan.FromSeconds(30) && !await read.Set<GuildBattle>().AnyAsync())
            {
                if(services.GetRequiredService<GuildCaptureHostedService>().ExecuteTask?.IsCompleted==true)break;
                await Task.Delay(25);
            }
            var battle=Assert.Single(await read.Set<GuildBattle>().AsNoTracking().ToArrayAsync());
            Assert.Equal("Prepared",battle.Phase);Assert.True(battle.Active);Assert.Null(battle.StartDigest);
            Assert.Throws<OutpostEvidenceUnavailableException>(()=>authority.RequireRuntimeGuild(GuildRuntimeUse.Simulation));
        }
        finally { await guarded.StopAsync(default); }

        f.Time.Advance(TimeSpan.FromMinutes(5));
        await services.GetRequiredService<GuildCaptureHostedService>().TickAsync(default);
        var expired=await read.Set<GuildBattle>().AsNoTracking().SingleAsync();
        Assert.Equal("CancelledAwaitingCleanup",expired.Phase);Assert.True(expired.Active);Assert.Null(expired.StartDigest);
        Assert.Equal("TechnicalBeforeStart",(await read.Set<GuildCampaign>().AsNoTracking().SingleAsync()).TerminalReason);
    }
}
