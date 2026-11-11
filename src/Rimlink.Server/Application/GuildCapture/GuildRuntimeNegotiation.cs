using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Grpc;

namespace Rimlink.Server.Application.GuildCapture;

internal static class GuildRuntimeNegotiation
{
    internal static GuildRuntimeTarget? Require(OutpostFeature feature,GuildRuntimeTarget? requested)
    {
        try{GuildRuntimeContracts.Require(requested);}catch(InvalidDataException){throw Refused();}
        string? stand=feature.GuildAcceptanceStand;
        if(stand is null){if(requested is not null)throw Refused();return null;}
        var build=feature.Readiness.BuildQualification;
        if(requested is null || requested.StandId!=stand || build.Mode!=OutpostRuntimeMode.IsolatedAcceptance
            || !build.AutomatedPrerequisitesVerified || !build.IsolationVerified)throw Refused();
        return requested.Clone();
    }
    internal static GuildRuntimeTarget? Target(OutpostFeature feature)=>feature.GuildAcceptanceStand is{} stand
        ?Require(feature,new(){Mode=GuildRuntimeMode.IsolatedAcceptanceV1,StandId=stand}):null;
    private static RpcException Refused()=>RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"explicit isolated stand selection required");
}
