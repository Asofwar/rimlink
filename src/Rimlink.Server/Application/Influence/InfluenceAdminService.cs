using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;

namespace Rimlink.Server.Application.Influence;

public sealed class InfluenceAdminService(InfluenceService influence)
{
    public Task<InfluenceSetting> Setting(Actor actor, InfluenceTarget target, CancellationToken ct) => influence.GetSetting(0, target, ct, actor);

    public Task<InfluenceReceipt> Disable(Actor actor, string identity, InfluenceTarget target, string requestId, InfluenceContext context, long version, CancellationToken ct) =>
        influence.Mutate(0, new InfluenceMutation { ClientRequestId = requestId, Publication = new InfluencePublication { Context = context, ExpectedVersion = version, Enabled = false } }, ct, actor, identity, target);

    public Task<InfluenceReceipt> Color(Actor actor, string identity, long allianceId, string worldId, int paletteIndex, string requestId, CancellationToken ct) =>
        influence.Mutate(0, new InfluenceMutation { ClientRequestId = requestId, Color = new InfluenceColor { WorldInstanceId = worldId, AllianceId = allianceId, PaletteIndex = paletteIndex } }, ct, actor, identity);
}
