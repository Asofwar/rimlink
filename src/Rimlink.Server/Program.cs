using Prometheus;
using Rimlink.Protocol;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
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

builder.WebHost.ConfigureKestrel(kestrel => KestrelSetup.Configure(kestrel, serverOptions, tlsOptions));

builder.Services.AddRimlinkStorage(new DataDirectory(serverOptions.DataDir));
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = Limits.MaxMessageBytes;
    options.Interceptors.Add<SessionInterceptor>();
});
builder.Services.AddHealthChecks();
builder.Services.AddHostedService<TlsStartupCheck>();

builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<BanService>();
builder.Services.AddScoped<LoginGate>();
builder.Services.AddHttpClient<ISteamTicketVerifier, SteamWebApiTicketVerifier>(client => client.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();
app.Services.MigrateRimlinkStorage();

app.UseSerilogRequestLogging();
app.UseHttpMetrics();
app.MapHealthChecks("/health");
app.MapMetrics("/metrics");
app.MapGrpcService<AuthGrpcService>();
app.MapGrpcService<EventsGrpcService>();

app.Run();

public partial class Program;
