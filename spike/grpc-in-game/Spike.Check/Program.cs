using Grpc.Net.Client;
using Rimlink.Spike;

// Plain .NET client: proves the server works before the in-game run.
var handler = new SocketsHttpHandler
{
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
};
using var channel = GrpcChannel.ForAddress("https://127.0.0.1:25560", new GrpcChannelOptions { HttpHandler = handler });
var reply = await new Ping.PingClient(channel).EchoAsync(new EchoRequest { Text = args.Length > 0 ? args[0] : "hi" });
Console.WriteLine($"{reply.Text} @ {reply.ServerTime}");
