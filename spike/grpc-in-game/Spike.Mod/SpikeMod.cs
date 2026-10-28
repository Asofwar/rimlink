using System.Net.Http;
using System.Text;
using Grpc.Net.Client;
using Verse;

namespace Rimlink.Spike;

// Throwaway: proves Grpc.Net.Client over HTTP/2 + TLS works inside RimWorld 1.7.
[StaticConstructorOnStartup]
public static class SpikeMod
{
    static SpikeMod()
    {
        string outFile = Path.Combine(GenFilePaths.SaveDataFolderPath, "rimlink-spike.txt");
        Log.Message("[RimlinkSpike] starting, result file: " + outFile);
        Task.Run(async () =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("runtime: " + Environment.Version);
            try
            {
                var handler = new SocketsHttpHandler
                {
                    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
                };
                using var channel = GrpcChannel.ForAddress("https://127.0.0.1:25560", new GrpcChannelOptions { HttpHandler = handler });
                var reply = await new Ping.PingClient(channel).EchoAsync(new EchoRequest { Text = "hi" });
                sb.AppendLine($"OK: {reply.Text} @ {reply.ServerTime}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("FAIL: " + ex);
            }
            File.WriteAllText(outFile, sb.ToString());
            Log.Message("[RimlinkSpike] " + sb.ToString().Split('\n')[1]);
        });
    }
}
