using Prometheus;
using Rimlink.Protocol;
using Rimlink.Server.Hosting;
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

builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = Limits.MaxMessageBytes);
builder.Services.AddHealthChecks();
builder.Services.AddHostedService<TlsStartupCheck>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseHttpMetrics();
app.MapHealthChecks("/health");
app.MapMetrics("/metrics");

app.Run();

public partial class Program;
