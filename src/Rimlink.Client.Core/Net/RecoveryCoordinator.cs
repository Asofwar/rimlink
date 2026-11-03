using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.State;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Net;

/// <summary>Independent state reads and replay after the caller has selected and proved its actual colony snapshot.</summary>
public sealed class RecoveryCoordinator
{
    private readonly RimlinkConnection connection;
    private readonly SharedState state;
    private readonly TransferOutbox? outbox;
    private readonly ILog log;
    private readonly Func<Action, Task> apply;
    private readonly Func<bool> isCurrent;
    private readonly SemaphoreSlim loading = new(1, 1);
    private LoginReply? liveLogin;
    public OutboxClients Clients { get; }
    public RecoveryCoordinator(RimlinkConnection connection, SharedState state, TransferOutbox? outbox, ILog log,
        Func<Action, Task> apply, Func<bool>? isCurrent = null)
    {
        this.connection = connection; this.state = state; this.outbox = outbox; this.log = log; this.apply = apply; this.isCurrent = isCurrent ?? (() => true);
        Clients = new(new EconomyClient(connection), new Raids.RaidClient(connection), new Visits.ShopClient(connection), new Live.LiveClient(connection, log),
            new Social.TradesClient(connection), new Incidents.IncidentClient(connection), new Procurement.ProcurementClient(connection),
            new Social.CommunityClient(connection), new Influence.InfluenceClient(connection));
    }

    public Task<ReplayReport> ReplayReadyAsync(CancellationToken ct) => outbox?.Replay(Clients, ct) ?? Task.FromResult(new ReplayReport(0, 0, 0));

    public async Task<bool> LoadStatesAsync(CancellationToken ct)
    {
        await loading.WaitAsync(ct);
        try
        {
            var login = connection.Login;
            bool complete = true;
            async Task Part(string name, Func<Task<Action>> fetch)
            {
                try
                {
                    var update = await fetch();
                    await apply(() =>
                    {
                        if (isCurrent() && ReferenceEquals(connection.Login, login)) update();
                        else complete = false;
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { complete = false; log.Warn(name + " recovery will retry: " + ex.Message); }
            }
            await Part("wallet", async () => { var value = await Clients.Economy.Wallet(ct); return () => state.Economy.SetWallet(value); });
            await Part("mailbox", async () => { var value = await Clients.Economy.Pending(ct); return () => state.Economy.LoadParcels(value); });
            await Part("economy rules", async () => { var value = await Clients.Economy.Rules(ct); return () => state.Economy.SetRules(value); });
            await Part("lots", async () => { var value = await Clients.Economy.Lots(new MarketQuery { SellerPlayerId = login?.Player.PlayerId ?? 0 }, ct); return () => state.Economy.LoadLots(value); });
            await Part("raids", async () =>
            {
                var pending = await Clients.Raids!.Pending(ct); var mine = await Clients.Raids.Mine(ct); var flags = await Clients.Raids.Flags(ct);
                return () => state.Raids.Load(pending, mine, flags);
            });
            await Part("incidents", async () =>
            {
                var pending = await Clients.Incidents!.Pending(ct); var mine = await Clients.Incidents.Mine(ct); var catalog = await Clients.Incidents.Catalog(ct);
                return () => state.Incidents.Load(pending, mine, catalog);
            });
            await Part("social", async () =>
            {
                var trades = await Clients.Trades!.List(ct); var blocks = await new Social.SocialClient(connection).ListBlocks(ct);
                return () => state.Social.Load(trades, blocks);
            });
            await Part("live", async () =>
            {
                LiveSession? current = null;
                try { current = await Clients.Live!.Current(ct); }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound || EconomyClient.CodeOf(ex) == ErrorCode.LiveGone) { }
                return () =>
                {
                    if (!ReferenceEquals(liveLogin, login)) { state.Live.Load(login?.PendingLiveEnds ?? [], current); liveLogin = login; }
                    else state.Live.SetCurrent(current);
                };
            });
            return complete;
        }
        finally { loading.Release(); }
    }
}
