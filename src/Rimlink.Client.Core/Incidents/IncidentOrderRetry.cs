using Rimlink.Client.Economy;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Incidents;

/// <summary>The pending order owns its intent and key until the server answers; UI selections cannot replace either.</summary>
public sealed class IncidentOrderRetry(TransferOutbox outbox)
{
    public OutboxEntry? Pending => outbox.Entries.FirstOrDefault(e => e.Kind == OutboxKind.IncidentOrder);

    public OutboxEntry Begin(OrderIncidentRequest intent)
    {
        if (Pending is { } pending) return pending;
        var request = intent.Clone();
        request.ClientRequestId = EconomyClient.NewRequestId();
        return outbox.Add(request);
    }
}
