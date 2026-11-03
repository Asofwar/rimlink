namespace Rimlink.Server.Application.Influence;

public static class InfluenceRefusalKeys
{
    public const string Conflict = "Influence.Conflict";
    public const string Ineligible = "Influence.Ineligible";
    public const string Capacity = "Influence.Capacity";
    public const string Invalid = "Influence.Invalid";
    public const string Intent = "Influence.Intent";
    public const string Unavailable = "Influence.Unavailable";

    public static string For(Protocol.V1.ErrorCode code) => code switch
    {
        Protocol.V1.ErrorCode.InfluenceConflict => Conflict,
        Protocol.V1.ErrorCode.InfluenceIneligible => Ineligible,
        Protocol.V1.ErrorCode.InfluenceCapacity => Capacity,
        Protocol.V1.ErrorCode.InfluenceInvalid => Invalid,
        Protocol.V1.ErrorCode.InfluenceIntent => Intent,
        Protocol.V1.ErrorCode.InfluenceUnavailable => Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}
