using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static class GuildRuntimeContracts
{
    public static void Require(GuildRuntimeTarget? target)
    {
        if(target is not null && (target.Mode!=GuildRuntimeMode.IsolatedAcceptanceV1 || !Guid.TryParseExact(target.StandId,"D",out var id)
            || id==Guid.Empty || id.ToString("D")!=target.StandId || target.CalculateSize()>64))throw new InvalidDataException("guild-runtime-target-invalid");
    }
}
