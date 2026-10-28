namespace Rimlink.Server.Grpc;

/// <summary>Permits per fixed window, by full gRPC method name. Anonymous calls are keyed by IP, the rest by account.</summary>
public static class RateLimitPolicy
{
    public static readonly (int Permits, TimeSpan Window) Default = (30, TimeSpan.FromSeconds(10));

    private static readonly Dictionary<string, (int, TimeSpan)> Table = new()
    {
        ["/rimlink.v1.Chat/Send"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Saves/Upload"] = (1, TimeSpan.FromSeconds(30)),
        ["/rimlink.v1.World/Settle"] = (1, TimeSpan.FromSeconds(10)),
        ["/rimlink.v1.Auth/Login"] = (10, TimeSpan.FromSeconds(60)),
        ["/rimlink.v1.Auth/Register"] = (5, TimeSpan.FromSeconds(60)),
        // Economy: one outgoing transfer per cooldown (Economy:SendCooldownSeconds is read by the interceptor for these two).
        ["/rimlink.v1.Transfer/Send"] = (1, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Market/Post"] = (1, TimeSpan.FromSeconds(5)),
    };

    public static (int Permits, TimeSpan Window) For(string method) => Table.TryGetValue(method, out var p) ? p : Default;
}
