using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Raids;

/// <summary>Typed raid calls over a connection; the session token travels in the channel credentials, every call has a 30 s deadline.</summary>
public sealed class RaidClient(RimlinkConnection connection)
{
    private static DateTime Until => DateTime.UtcNow + Economy.EconomyClient.Deadline;

    public Task<Raid> Launch(LaunchRequest request, CancellationToken ct) => connection.RaidsApi.LaunchAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;
    /// <summary>Raids aimed at me that have not landed; descriptors only.</summary>
    public Task<RaidList> Pending(CancellationToken ct) => connection.RaidsApi.PendingAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Raid> Get(long raidId, CancellationToken ct) => connection.RaidsApi.GetAsync(new RaidRef { RaidId = raidId }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<Raid> Announce(long raidId, long gameTick, CancellationToken ct) =>
        connection.RaidsApi.AnnounceAsync(new RaidTick { RaidId = raidId, GameTick = gameTick }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task Land(long raidId, long gameTick, CancellationToken ct) =>
        connection.RaidsApi.LandAsync(new RaidTick { RaidId = raidId, GameTick = gameTick }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task Report(RaidReport report, CancellationToken ct) => connection.RaidsApi.ReportAsync(report, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<RaidList> Mine(CancellationToken ct) => connection.RaidsApi.MineAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<PlayerFlags> Flags(CancellationToken ct) => connection.RaidsApi.FlagsAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<PlayerFlags> SetPeaceful(bool peaceful, CancellationToken ct) =>
        connection.RaidsApi.SetPeacefulAsync(new PeacefulRequest { Peaceful = peaceful }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<PlayerFlags> SetVisitsClosed(bool closed, CancellationToken ct) =>
        connection.RaidsApi.SetVisitsClosedAsync(new VisitsClosedRequest { Closed = closed }, deadline: Until, cancellationToken: ct).ResponseAsync;
}
