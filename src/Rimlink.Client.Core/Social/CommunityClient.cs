using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Social;

public sealed class CommunityClient(RimlinkConnection connection)
{
    private static DateTime Until => DateTime.UtcNow + EconomyClient.Deadline;
    public Task<CommunityState> Get(CancellationToken ct) => connection.CommunityApi.GetAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<PublicProfile> Profile(long playerId, CancellationToken ct) => connection.CommunityApi.ProfileAsync(new PlayerRef { PlayerId = playerId }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<PublicProfiles> Directory(CancellationToken ct) => connection.CommunityApi.DirectoryAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<CommunityReceipt> Mutate(CommunityRequest request, CancellationToken ct) => connection.CommunityApi.MutateAsync(request, deadline: Until, cancellationToken: ct).ResponseAsync;

    /// <summary>Write the exact intent before sending; an unknown answer remains on disk for replay.</summary>
    public async Task<CommunityReceipt> Submit(CommunityRequest request, TransferOutbox outbox, CancellationToken ct)
    {
        if (request.ClientRequestId.Length == 0) request.ClientRequestId = Guid.NewGuid().ToString("N");
        var entry = outbox.Add(request);
        try
        {
            var reply = await Mutate(request, ct);
            outbox.Remove(entry.Id);
            return reply;
        }
        catch (global::Grpc.Core.RpcException ex) when (TransferOutbox.IsDefinitive(ex))
        {
            outbox.Refuse(entry.Id, RpcErrorDetail.Read(ex));
            throw;
        }
    }
}
