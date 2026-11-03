using Rimlink.Protocol.V1;

namespace Rimlink.Client.Mods.Delivery;

public static class ModpackJoinReadiness
{
    public static bool MayContinue(bool deliveryVerified, Role role, bool automaticRejoin) =>
        deliveryVerified || role == Role.Admin && !automaticRejoin;
}
