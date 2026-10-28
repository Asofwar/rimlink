using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Rimlink.Spike;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenLocalhost(25560, l =>
    {
        l.Protocols = HttpProtocols.Http2;
        l.UseHttps(); // ASP.NET Core dev certificate
    });
});
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<PingService>();
app.Run();

sealed class PingService : Ping.PingBase
{
    public override Task<EchoReply> Echo(EchoRequest request, ServerCallContext context)
    {
        Console.WriteLine($"Echo from {context.Peer}: {request.Text}");
        return Task.FromResult(new EchoReply { Text = request.Text, ServerTime = DateTime.UtcNow.ToString("O") });
    }
}
