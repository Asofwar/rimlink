using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;

namespace Rimlink.Server.Application.Outposts;

public static class OutpostRefusalKeys
{
    public static void RequireReady(Session session, OutpostFeature feature)
    {
        if (session.OutpostVersion != OutpostRecoveryData.CurrentVersion)
            throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.OutpostClientRequired, RefusalKeys.OutpostsClient,
                "outpost custody client version required");
        if (!feature.IsAvailable || feature.Startup.State != OutpostStartupState.Ready)
            throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.OutpostUnavailable, RefusalKeys.OutpostsUnavailable,
                "outpost custody graph is not available");
    }
}
