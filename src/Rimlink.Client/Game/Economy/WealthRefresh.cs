using Grpc.Core;
using Rimlink.Client.UI;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// Keeps the server-held wealth the storyteller sees current: once something changed what the server holds (an event, or
/// this client's own call), the wallet is fetched again, at most every few seconds and less often after a failure.
/// </summary>
public static class WealthRefresh
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromSeconds(60);
    private static DateTime next = DateTime.MinValue;
    private static bool fetching;

    public static void Tick()
    {
        var state = RimlinkSession.State.Economy;
        if (!state.WealthStale || fetching || DateTime.UtcNow < next || RimlinkSession.Economy is not { } economy) return;
        fetching = true;
        next = DateTime.UtcNow + Interval;
        _ = Task.Run(async () =>
        {
            try
            {
                var wallet = await economy.Wallet(CancellationToken.None);
                MainThread.Post(() => { state.SetWallet(wallet); fetching = false; });
            }
            catch (RpcException)
            {
                // A paused economy refuses the wallet: try again later, the stale flag stays.
                MainThread.Post(() => { next = DateTime.UtcNow + AfterFailure; fetching = false; });
            }
        });
    }
}
