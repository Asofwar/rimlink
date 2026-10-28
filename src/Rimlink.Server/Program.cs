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
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.Section));
builder.Services.Configure<EconomyOptions>(builder.Configuration.GetSection(EconomyOptions.Section));

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
builder.Services.AddSingleton<FreezeState>();
builder.Services.AddSingleton<ModpackDefs>();
builder.Services.AddScoped<EconomyGate>();
builder.Services.AddScoped<ParcelValidator>();
builder.Services.AddScoped<WalletService>();
builder.Services.AddScoped<MailboxService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<MarketService>();
builder.Services.AddHostedService<LotExpiryService>();
builder.Services.AddSingleton<PresenceNotifier>();
builder.Services.AddHttpClient<ISteamTicketVerifier, SteamWebApiTicketVerifier>(client => client.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();
app.Services.MigrateRimlinkStorage();
ModpackService.LoadDefs(app.Services);
app.Services.GetRequiredService<PresenceNotifier>(); // subscribes to session events

app.UseSerilogRequestLogging();
app.UseHttpMetrics();
app.MapHealthChecks("/health");
app.MapMetrics("/metrics");
app.MapGrpcService<AuthGrpcService>();
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
app.MapAdminApi();

app.Run();

public partial class Program;
