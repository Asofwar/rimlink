using Prometheus;
using Rimlink.Protocol;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Backup;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Rest;
using Rimlink.Server.Storage;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("Rimlink__");

var serverOptions = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
var tlsOptions = builder.Configuration.GetSection(TlsOptions.Section).Get<TlsOptions>() ?? new TlsOptions();
Directory.CreateDirectory(Path.Combine(serverOptions.DataDir, "logs"));

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(serverOptions.DataDir, "logs", "server-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14));

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
builder.Services.Configure<TlsOptions>(builder.Configuration.GetSection(TlsOptions.Section));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddOptions<AdminOptions>().Bind(builder.Configuration.GetSection(AdminOptions.Section))
    .Validate(o => o.FeedBufferSize is >= 100 and <= 100000 && o.FeedPingSeconds is >= 1 and <= 300, "Admin:FeedBufferSize must be 100-100000 and Admin:FeedPingSeconds 1-300")
    .ValidateOnStart();
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.Section));
builder.Services.Configure<EconomyOptions>(builder.Configuration.GetSection(EconomyOptions.Section));
builder.Services.Configure<RaidOptions>(builder.Configuration.GetSection(RaidOptions.Section));
builder.Services.Configure<LiveOptions>(builder.Configuration.GetSection(LiveOptions.Section));
builder.Services.Configure<PanelOptions>(builder.Configuration.GetSection(PanelOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new UtcDateTimeConverter()));
// Built from the bound options (test settings are not visible to the values read above before Build()).
builder.Services.AddSingleton(sp => new ServerCertificate(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value,
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TlsOptions>>().Value));

builder.WebHost.ConfigureKestrel(kestrel => KestrelSetup.Configure(kestrel, serverOptions, tlsOptions));

builder.Services.AddRimlinkStorage(new DataDirectory(serverOptions.DataDir));
builder.Services.AddGrpc(options =>
{
    // A 1 MB save chunk plus its protobuf framing must still fit.
    options.MaxReceiveMessageSize = Limits.MaxMessageBytes + 16 * 1024;
    options.Interceptors.Add<SessionInterceptor>();
    options.Interceptors.Add<RateLimitInterceptor>();
});
builder.Services.AddHealthChecks();
builder.Services.AddHostedService<TlsStartupCheck>();
builder.Services.AddHostedService<ServerLock>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<BackupScheduler>();

builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<RateLimitInterceptor>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<BanService>();
builder.Services.AddScoped<LoginGate>();
builder.Services.AddScoped<WorldService>();
builder.Services.AddScoped<SaveStore>();
builder.Services.AddScoped<ModpackService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<AdminOperations>();
builder.Services.AddScoped<AdminCommandRunner>();
builder.Services.AddScoped<AdminDirectory>();
builder.Services.AddScoped<ServerStatus>();
builder.Services.AddSingleton<PublicStatusCache>();
builder.Services.AddSingleton<FreezeState>();
builder.Services.AddSingleton<Rimlink.Server.Application.Raids.RaidFreezeState>();
builder.Services.AddSingleton<ModpackDefs>();
builder.Services.AddScoped<EconomyGate>();
builder.Services.AddScoped<ParcelValidator>();
builder.Services.AddScoped<WalletService>();
builder.Services.AddScoped<MailboxService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<MarketService>();
builder.Services.AddHostedService<LotExpiryService>();
builder.Services.AddScoped<Rimlink.Server.Application.Raids.RaidGate>();
builder.Services.AddScoped<Rimlink.Server.Application.Raids.RaidRules>();
builder.Services.AddScoped<Rimlink.Server.Application.Raids.RaidService>();
builder.Services.AddScoped<Rimlink.Server.Application.Raids.RaidReportValidator>();
builder.Services.AddHostedService<Rimlink.Server.Application.Raids.RaidExpiryService>();
builder.Services.AddScoped<Rimlink.Server.Application.Visits.VisitService>();
builder.Services.AddScoped<Rimlink.Server.Application.Visits.ShopService>();
builder.Services.AddScoped<Rimlink.Server.Application.Live.LiveSessionService>();
builder.Services.AddSingleton<Rimlink.Server.Application.Live.LiveTimeoutService>();
builder.Services.AddSingleton<Rimlink.Server.Application.Live.LiveRelay>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Rimlink.Server.Application.Live.LiveTimeoutService>());
builder.Services.AddSingleton<PresenceNotifier>();
builder.Services.AddSingleton<Rimlink.Server.Application.Feed.AdminFeed>();
builder.Services.AddSingleton<ApiTokens>();
builder.Services.AddSingleton<PanelSessions>();
builder.Services.AddHttpClient<ISteamTicketVerifier, SteamWebApiTicketVerifier>(client => client.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();
app.Services.GetRequiredService<ApiTokens>(); // validates Admin:Tokens: a bad entry stops the start
app.Services.MigrateRimlinkStorage();
ModpackService.LoadDefs(app.Services);
app.Services.GetRequiredService<PresenceNotifier>(); // subscribes to session events
app.Services.GetRequiredService<Rimlink.Server.Application.Feed.AdminFeed>(); // server.started is the feed's first event

app.UseSerilogRequestLogging();
app.UseHttpMetrics();
app.UsePanelStaticFiles();
app.MapHealthChecks("/health");
app.MapMetrics("/metrics");
app.MapGrpcService<AuthGrpcService>();
app.MapGrpcService<LiveGrpcService>();
app.MapGrpcService<EventsGrpcService>();
app.MapGrpcService<WorldGrpcService>();
app.MapGrpcService<SavesGrpcService>();
app.MapGrpcService<ModpackGrpcService>();
app.MapGrpcService<ChatGrpcService>();
app.MapGrpcService<AdminGrpcService>();
app.MapGrpcService<WalletGrpcService>();
app.MapGrpcService<MailboxGrpcService>();
app.MapGrpcService<TransferGrpcService>();
app.MapGrpcService<MarketGrpcService>();
app.MapGrpcService<RaidsGrpcService>();
app.MapGrpcService<VisitsGrpcService>();
app.MapGrpcService<ShopGrpcService>();
app.MapAdminApi();
app.MapAdminDirectoryApi();
app.MapAdminFeedApi();
app.MapPanelApi();
app.MapPublicApi();

app.Run();

public partial class Program;
