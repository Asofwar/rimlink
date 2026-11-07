using Prometheus;
using Rimlink.Server.Application.Procurement;
using Rimlink.Protocol;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Auctions;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.Social;
using Rimlink.Server.Application.World;
using Rimlink.Server.Backup;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Rest;
using Rimlink.Server.Storage;
using Serilog;

using Rimlink.Server.Application.Outposts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Rimlink.Server.Hosting;

public static class RimlinkApplication
{
    public static Task<WebApplication> BuildAsync(string[] args) => BuildCoreAsync(args, null);
    internal static Task<WebApplication> BuildIsolatedAsync(string[] args, IOutpostAcceptanceComposition acceptance) => BuildCoreAsync(args, acceptance);

    private static async Task<WebApplication> BuildCoreAsync(string[] args, IOutpostAcceptanceComposition? acceptance)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(Program).Assembly.FullName,
            EnvironmentName = acceptance is null ? null : "OutpostAcceptance", ContentRootPath = acceptance is null ? null : AppContext.BaseDirectory,
            WebRootPath = acceptance is null ? null : Path.Combine(AppContext.BaseDirectory, "wwwroot") });
        builder.Configuration.AddEnvironmentVariables("Rimlink__");
        acceptance?.ConfigureConfiguration(builder.Configuration);

        var serverOptions = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
        var tlsOptions = builder.Configuration.GetSection(TlsOptions.Section).Get<TlsOptions>() ?? new TlsOptions();
        acceptance?.ValidateConfiguration(serverOptions, tlsOptions, builder.Configuration);
        if (acceptance is not null && acceptance.Qualification.Observe() is not { Mode: OutpostRuntimeMode.IsolatedAcceptance,
                AutomatedPrerequisitesVerified: true, IsolationVerified: true })
            throw new InvalidDataException("outpost-acceptance-composition-unqualified");
        Directory.CreateDirectory(Path.Combine(serverOptions.DataDir, "logs"));

        builder.Host.UseSerilog((context, services, config) => config
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(serverOptions.DataDir, "logs", "server-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14),
            preserveStaticLogger: true);

        builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
        builder.Services.Configure<TlsOptions>(builder.Configuration.GetSection(TlsOptions.Section));
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
        builder.Services.AddOptions<AdminOptions>().Bind(builder.Configuration.GetSection(AdminOptions.Section))
            .Validate(o => o.FeedBufferSize is >= 100 and <= 100000 && o.FeedPingSeconds is >= 1 and <= 300, "Admin:FeedBufferSize must be 100-100000 and Admin:FeedPingSeconds 1-300")
            .ValidateOnStart();
        builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.Section));
        builder.Services.Configure<SaveStorageOptions>(builder.Configuration.GetSection(SaveStorageOptions.Section));
        builder.Services.AddOptions<EconomyOptions>().Bind(builder.Configuration.GetSection(EconomyOptions.Section))
            .Validate(o => o.GiftDecisionDays >= 1 && o.TradeLifetimeDays >= 1 && o.MaxOpenTrades >= 1, "Economy:GiftDecisionDays, Economy:TradeLifetimeDays and Economy:MaxOpenTrades must be at least 1")
            .ValidateOnStart();
        builder.Services.AddOptions<SocialOptions>().Bind(builder.Configuration.GetSection(SocialOptions.Section))
            .Validate(o => o.MaxBlocks >= 1, "Social:MaxBlocks must be at least 1")
            .ValidateOnStart();
        builder.Services.AddOptions<IncidentOptions>().Bind(builder.Configuration.GetSection(IncidentOptions.Section))
            .Validate(o => o.CatalogIsValid(), "Incidents:Catalog entries need a unique Id, an IncidentDef, a Kind of Hostile, Weather or Kind and positive multipliers")
            .ValidateOnStart();
        builder.Services.Configure<RaidOptions>(builder.Configuration.GetSection(RaidOptions.Section));
        builder.Services.Configure<LiveOptions>(builder.Configuration.GetSection(LiveOptions.Section));
        builder.Services.Configure<PanelOptions>(builder.Configuration.GetSection(PanelOptions.Section));
        builder.Services.AddOptions<IntegrityOptions>().Bind(builder.Configuration.GetSection(IntegrityOptions.Section))
            .Validate(IntegrityOptions.IsValid, "Integrity:WealthFreezePerDay must be 0 or at least Integrity:WealthWarnPerDay, and neither negative")
            .ValidateOnStart();
        builder.Services.AddOptions<HygieneOptions>().Bind(builder.Configuration.GetSection(HygieneOptions.Section))
            .Validate(o => o.IsValid, "Hygiene: days, wealth and age must be 0 or more, SweepHours 1-168, WealthTiers four increasing positive numbers")
            .ValidateOnStart();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new UtcDateTimeConverter()));
        // Built from the bound options (test settings are not visible to the values read above before Build()).
        builder.Services.AddSingleton(sp => new ServerCertificate(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value,
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TlsOptions>>().Value));

        builder.WebHost.ConfigureKestrel(kestrel => { if (acceptance is null) KestrelSetup.Configure(kestrel, serverOptions, tlsOptions); else KestrelSetup.ConfigureLoopback(kestrel, serverOptions, tlsOptions); });

        builder.Services.AddRimlinkStorage(new DataDirectory(serverOptions.DataDir));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddGrpc(options =>
        {
            // A 1 MB save chunk plus its protobuf framing must still fit.
            options.MaxReceiveMessageSize = Limits.MaxMessageBytes + 16 * 1024;
            options.Interceptors.Add<SessionInterceptor>();
            options.Interceptors.Add<RateLimitInterceptor>();
        });
        builder.Services.AddHealthChecks();
        builder.Services.AddHostedService<TlsStartupCheck>();
        builder.Services.AddSingleton<ServerLock>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerLock>());
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddOutpostDomainHostedService<BackupScheduler>();

        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<EventBus>();
        builder.Services.AddSingleton<RateLimitInterceptor>();
        builder.Services.AddScoped<AuthService>();
        builder.Services.AddScoped<BanService>();
        builder.Services.AddScoped<LoginGate>();
        builder.Services.AddScoped<WorldService>();
        builder.Services.AddSingleton<SaveFiles>();
        builder.Services.AddScoped<SaveCompactor>();
        builder.Services.AddOutpostDomainHostedService<SaveCompactionService>();
        builder.Services.AddScoped<SaveStore>();
        builder.Services.AddScoped<Rimlink.Server.Application.Integrity.IntegrityService>();
        builder.Services.AddScoped<ModpackService>();
        builder.Services.AddSingleton<DeliveryRuntime>();
        builder.Services.AddScoped<ModpackDeliveryService>();
        builder.Services.AddScoped<ChatService>();
        builder.Services.AddCommunity();
        builder.Services.AddScoped<Rimlink.Server.Application.Social.SocialService>();
        builder.Services.AddScoped<AdminOperations>();
        builder.Services.AddScoped<AdminAudit>();
        builder.Services.AddScoped<Rimlink.Server.Application.Saves.SaveArchive>();
        builder.Services.AddSingleton<Rimlink.Server.Application.World.ColonyLocks>();
        builder.Services.AddScoped<Rimlink.Server.Application.World.ColonyArchive>();
        builder.Services.AddScoped<Rimlink.Server.Application.World.ColonyHygiene>();
        builder.Services.AddScoped<Rimlink.Server.Application.Away.AwaySummaryService>();
        builder.Services.AddOutpostDomainHostedService<Rimlink.Server.Application.World.ColonyHygieneService>();
        builder.Services.AddScoped<AdminCommandRunner>();
        builder.Services.AddScoped<AdminDirectory>();
        builder.Services.AddScoped<ServerStatus>();
        builder.Services.AddSingleton<PublicStatusCache>();
        builder.Services.AddSingleton<FreezeState>();
        builder.Services.AddSingleton<Rimlink.Server.Application.Raids.RaidFreezeState>();
        builder.Services.AddSingleton<ModpackDefs>();
        builder.Services.AddSingleton<ForbiddenDefs>();
        builder.Services.AddScoped<EconomyGate>();
        builder.Services.AddScoped<PlayerFreeze>();
        builder.Services.AddScoped<ParcelValidator>();
        builder.Services.AddScoped<WalletService>();
        builder.Services.AddScoped<ServerWealth>();
        builder.Services.AddScoped<EconomyGuard>();
        builder.Services.AddScoped<ForbiddenDefsService>();
        builder.Services.AddScoped<Rimlink.Server.Application.Incidents.IncidentService>();
        builder.Services.AddScoped<MailboxService>();
        builder.Services.AddScoped<TransferService>();
        builder.Services.AddScoped<MarketService>();
        builder.Services.AddAuctions();
        builder.Services.AddOutpostDomainHostedService<LotExpiryService>();
        builder.Services.AddScoped<GiftService>();
        builder.Services.AddScoped<Rimlink.Server.Application.Trades.TradeService>();
        builder.Services.AddOutpostDomainHostedService<SafeTradesSweep>();
        builder.Services.AddScoped<Rimlink.Server.Application.Raids.RaidGate>();
        builder.Services.AddScoped<Rimlink.Server.Application.Raids.RaidRules>();
        builder.Services.AddScoped<Rimlink.Server.Application.Raids.RaidService>();
        builder.Services.AddScoped<Rimlink.Server.Application.Raids.RaidReportValidator>();
        builder.Services.AddOutpostDomainHostedService<Rimlink.Server.Application.Raids.RaidExpiryService>();
        builder.Services.AddOutpostDomainHostedService<Rimlink.Server.Application.Incidents.IncidentExpiryService>();
        builder.Services.AddScoped<Rimlink.Server.Application.Visits.VisitService>();
        builder.Services.AddScoped<Rimlink.Server.Application.Visits.ShopService>();
        builder.Services.AddScoped<Rimlink.Server.Application.Live.LiveSessionService>();
        builder.Services.AddScoped<Rimlink.Server.Application.Colonies.ColonyService>();
        builder.Services.AddSingleton<Rimlink.Server.Application.Live.LiveTimeoutService>();
        builder.Services.AddSingleton<Rimlink.Server.Application.Live.LiveRelay>();
        builder.Services.AddSingleton<Rimlink.Server.Application.Live.LiveStateLock>();
        builder.Services.AddOutpostDomainHostedService<Rimlink.Server.Application.Live.LiveTimeoutService>();
        builder.Services.AddSingleton<PresenceNotifier>();
        builder.Services.AddSingleton<SessionEndRecorder>();
        builder.Services.AddSingleton<Rimlink.Server.Application.Feed.AdminFeed>();
        builder.Services.AddSingleton<ApiTokens>();
        builder.Services.AddSingleton<PanelSessions>();
        builder.Services.AddHttpClient<ISteamTicketVerifier, SteamWebApiTicketVerifier>(client => client.Timeout = TimeSpan.FromSeconds(10));

        builder.Services.AddProcurement();
        Rimlink.Server.Application.Influence.InfluenceFeature.AddInfluence(builder.Services);

        Rimlink.Server.Application.Outposts.OutpostFeature.Register(builder.Services, builder.Configuration);

        if (acceptance is not null)
        {
            builder.Services.AddSingleton<IOutpostBuildQualification>(acceptance.Qualification);
            builder.Services.Replace(ServiceDescriptor.Singleton<OutpostClock>(sp =>
                new(acceptance.OutpostTimeProvider ?? sp.GetRequiredService<TimeProvider>())));
            acceptance.ConfigureServices(builder.Services);
        }
        var app = builder.Build();
        try
        {
            app.Services.GetRequiredService<ServerLock>().Acquire();
            app.Services.GetRequiredService<ApiTokens>(); // validates Admin:Tokens: a bad entry stops the start
            app.Services.MigrateRimlinkStorage();
            await Rimlink.Server.Application.Outposts.OutpostFeature.CheckStartupAsync(app.Services, CancellationToken.None);
            ModpackService.LoadDefs(app.Services);
            ForbiddenDefsService.Load(app.Services);
            app.Services.GetRequiredService<PresenceNotifier>(); // subscribes to session events
            app.Services.GetRequiredService<SessionEndRecorder>(); // subscribes to session ends
            app.Services.GetRequiredService<Rimlink.Server.Application.Feed.AdminFeed>(); // server.started is the feed's first event
        }
        catch (Exception error)
        {
            // Persist the original refusal before cleanup (test minimal-host startup can race a disposed provider).
            app.Logger.LogCritical(error, "Server initialization failed");
            await app.DisposeAsync();
            throw;
        }

        app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
        app.UseHttpMetrics();
        app.UsePanelStaticFiles();
        app.MapHealthChecks("/health");
        app.MapMetrics("/metrics");
        app.MapGrpcService<AuthGrpcService>();
        app.MapGrpcService<LiveGrpcService>();
        app.MapGrpcService<ColoniesGrpcService>();
        app.MapGrpcService<EventsGrpcService>();
        app.MapGrpcService<WorldGrpcService>();
        app.MapGrpcService<OutpostsGrpcService>();
        app.MapGrpcService<SavesGrpcService>();
        app.MapGrpcService<ModpackGrpcService>();
        app.MapGrpcService<ChatGrpcService>();
        app.MapGrpcService<AdminGrpcService>();
        app.MapGrpcService<WalletGrpcService>();
        app.MapGrpcService<MailboxGrpcService>();
        app.MapGrpcService<TransferGrpcService>();
        app.MapGrpcService<MarketGrpcService>();
        app.MapAuctions();
        app.MapGrpcService<RaidsGrpcService>();
        app.MapGrpcService<IncidentsGrpcService>();
        app.MapGrpcService<VisitsGrpcService>();
        app.MapGrpcService<ShopGrpcService>();
        app.MapGrpcService<SocialGrpcService>();
        app.MapCommunity();
        app.MapGrpcService<TradesGrpcService>();
        app.MapGrpcService<AwayGrpcService>();
        app.MapProcurement();
        Rimlink.Server.Application.Influence.InfluenceFeature.MapInfluence(app);
        app.MapAdminApi();
        app.MapOutpostAdmin();
        app.MapModpackDelivery();
        app.MapAdminDirectoryApi();
        app.MapColonyApi();
        app.MapAdminFeedApi();
        app.MapPanelApi();
        app.MapPublicApi();

        return app;
    }
}
