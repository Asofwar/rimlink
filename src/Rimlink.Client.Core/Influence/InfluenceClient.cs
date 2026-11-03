using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Influence;

public sealed class InfluenceClient(RimlinkConnection connection)
{
    public static string? RestoreNoticeKey(ColonyRestore? restore) =>
        restore is { Restored: true, InfluenceNoticeKey: "Influence.CapacityRestore" } ? "Rimlink.Influence.CapacityRestore" : null;

    private static DateTime Until => DateTime.UtcNow + EconomyClient.Deadline;
    public Task<InfluenceSnapshot> Get(CancellationToken ct) => connection.InfluenceApi.GetAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<InfluenceSetting> GetSetting(InfluenceTarget target, CancellationToken ct) => connection.InfluenceApi.GetSettingAsync(target, deadline: Until, cancellationToken: ct).ResponseAsync;

    public async Task<InfluenceReceipt> Mutate(InfluenceMutation request, CancellationToken ct)
    {
        var receipt = await connection.InfluenceApi.MutateAsync(request, deadline: Until, cancellationToken: ct);
        if (receipt.Error is { } error)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message), new Metadata { { RpcErrorDetail.Trailer, error.ToByteArray() } });
        return receipt;
    }

    public async Task<InfluenceReceipt> Submit(InfluenceMutation request, TransferOutbox outbox, CancellationToken ct)
    {
        if (request.ClientRequestId.Length == 0) request.ClientRequestId = Guid.NewGuid().ToString("N");
        var entry = outbox.Add(request);
        try
        {
            var receipt = await Mutate(request, ct);
            outbox.Remove(entry.Id);
            return receipt;
        }
        catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
        {
            outbox.Refuse(entry.Id, RpcErrorDetail.Read(ex));
            throw;
        }
    }
}
