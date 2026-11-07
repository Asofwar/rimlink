using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostRuntimeReadinessTests
{
    [Fact]
    public async Task An_admin_only_host_starts_its_deferred_writer_only_after_real_ready_observation()
    {
        var feature = new OutpostFeature(Options.Create(new OutpostOptions()), new OutpostTestQualification(), OutpostTestQualification.Integrations)
            { Startup = new(OutpostStartupState.NoActiveWorld) };
        feature.ObserveDomain(OutpostDomainFormat.V2, true);
        var tracker = new Tracker();
        using var provider = new ServiceCollection().AddSingleton(_ => { tracker.Constructed++; return new Writer(tracker); }).BuildServiceProvider();
        using var gate = new OutpostDomainHostedService<Writer>(provider, feature, NullLogger<OutpostDomainHostedService<Writer>>.Instance);
        await gate.StartAsync(default); Assert.Equal(0, tracker.Constructed);
        feature.Startup = new(OutpostStartupState.Ready);
        var deadline = Stopwatch.StartNew();
        while (Volatile.Read(ref tracker.Started) == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        try { Assert.Equal(1, tracker.Constructed); Assert.Equal(1, tracker.Started); }
        finally { await gate.StopAsync(default); }
        Assert.Equal(1, tracker.Stopped);
    }
    [Fact]
    public void A_legacy_writer_rechecks_readiness_after_protected_history_appears()
    {
        var feature = new OutpostFeature(Options.Create(new OutpostOptions()), new OutpostProductQualification())
            { HasHistoryObservation = false, Startup = new(OutpostStartupState.RequiresInitialization) };
        using var provider = new ServiceCollection().AddSingleton(feature).BuildServiceProvider();
        Assert.True(OutpostDomainHostedServices.CanWrite(provider));
        feature.ObserveDomain(OutpostDomainFormat.V2, true); feature.Startup = new(OutpostStartupState.Ready);
        Assert.False(OutpostDomainHostedServices.CanWrite(provider));
    }
    [Fact]
    public async Task Pending_product_holds_hosted_writers_before_their_constructors_run()
    {
        var feature = new OutpostFeature(Options.Create(new OutpostOptions { Enabled = true }), new OutpostProductQualification(), OutpostTestQualification.Integrations)
            { Startup = new(OutpostStartupState.Ready) };
        feature.ObserveDomain(OutpostDomainFormat.V2, true);
        var tracker = new Tracker(); var services = new ServiceCollection();
        services.AddSingleton(_ => { tracker.Constructed++; return new Writer(tracker); });
        using var provider = services.BuildServiceProvider();
        using var gate = new OutpostDomainHostedService<Writer>(provider, feature, NullLogger<OutpostDomainHostedService<Writer>>.Instance);
        await gate.StartAsync(default); await gate.StopAsync(default);
        Assert.Equal(0, tracker.Constructed); Assert.Equal(0, tracker.Started); Assert.Equal(0, tracker.Stopped);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Real_product_with_enabled_configuration_refuses_protected_login_and_mutation(bool activate)
    {
        using var h = await SnapshotHarness.Create(); var save = h.Snapshot(); await h.Accept(save); var root = h.H;
        var account = await root.Db.Accounts.SingleAsync(); account.Login = "pending-owner"; account.PasswordHash = PasswordHasher.Hash("pw");
        await root.Db.SaveChangesAsync();
        if (activate) await OutpostUnitComposition.Activate(root);
        using var server = HygieneFixtures.HygieneServer().WithoutWorld().WithDataDir(root.Data.Root).KeepDataDir()
            .WithSetting("Outposts:Enabled", "true").WithSetting("Outposts:Mode", "IsolatedAcceptance")
            .WithSetting("Outposts:BuildQualification", "Qualified")
            .WithSetting("Outposts:AnchorDirectory", Path.GetDirectoryName(root.Anchor.DatabasePath))
            .WithSetting("Outposts:StorageContract", "sqlite-local-v1")
            .WithServices(s => { s.AddSingleton(root.Files); s.AddSingleton(root.Owners); s.AddSingleton(root.Anchor); });
        using var scope = server.Services.CreateScope(); var provider = scope.ServiceProvider;
        var feature = provider.GetRequiredService<OutpostFeature>(); var decision = feature.Readiness;
        Assert.Equal(OutpostRuntimeMode.Product, decision.Mode); Assert.Equal(OutpostQualificationState.Pending, decision.BuildQualification.State);
        Assert.False(decision.AdvertiseCapability); Assert.False(decision.CanResolveCustody); Assert.False(decision.CanRunDomainWriters);
        Assert.Equal(activate ? OutpostStartupState.Ready : OutpostStartupState.FormatActivationRequired, decision.Startup.State);
        var context = new LoginContext(ProtocolVersion.Current, null, [], "127.0.0.1")
            { RecoveryVersion = SaveRecoveryData.CurrentVersion, OutpostVersion = OutpostRecoveryData.CurrentVersion };
        var result = Assert.IsType<LoginResult.Failure>(await provider.GetRequiredService<AuthService>().LoginWithPassword("pending-owner", "pw", context));
        Assert.Equal(ErrorCode.OutpostUnavailable, result.Code);
        Assert.Empty(provider.GetRequiredService<SessionRegistry>().All);
        Assert.Empty(await root.Db.Sessions.ToArrayAsync());
        var authority = provider.GetRequiredService<OutpostAuthority>();
        await using var lease = await authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => provider.GetRequiredService<OutpostAssignmentService>()
            .ScopeAsync(lease, 1, save.Recovery.Outposts.Metadata.Scope.Clone(), default));
    }

    private sealed class Tracker { internal int Constructed; internal int Started; internal int Stopped; }
    private sealed class Writer(Tracker tracker) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) { tracker.Started++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { tracker.Stopped++; return Task.CompletedTask; }
    }
}
