using Grpc.Core;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Net;

/// <summary>
/// Back-off after a dropped connection: 1, 2, 4 … doubling up to 60 s, then every 60 s for as long as the game runs.
/// After <see cref="OfferExitAfter"/> the game offers to leave to the menu with a local save; the attempts go on.
/// </summary>
public sealed class ReconnectPolicy(TimeSpan firstDelay, TimeSpan maxDelay, TimeSpan offerExitAfter)
{
    public static ReconnectPolicy Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2));

    public TimeSpan OfferExitAfter => offerExitAfter;

    public IEnumerable<TimeSpan> Delays()
    {
        var next = firstDelay;
        while (true)
        {
            yield return next < maxDelay ? next : maxDelay;
            if (next < maxDelay) next += next;
        }
    }

    /// <summary>
    /// The server turned the login down for good (ban, credentials, server password, protocol, the world deleted): retrying
    /// cannot help, and a world created anew must not take this colony in. A full server may change, like any network or
    /// server failure, and so may a Steam ticket the server could not verify (Steam's Web API down answers the same way).
    /// </summary>
    public static bool IsRefusal(RpcException ex, bool steamTicket = false) => ex.StatusCode switch
    {
        StatusCode.Unauthenticated => !steamTicket,
        StatusCode.PermissionDenied or StatusCode.InvalidArgument => true,
        StatusCode.FailedPrecondition => Economy.EconomyClient.CodeOf(ex) is not ErrorCode.ServerFull,
        _ => false,
    };
}
