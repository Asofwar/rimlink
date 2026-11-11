using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Hosting;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.GuildCapture;

namespace Rimlink.Server.Grpc;

public sealed class BattleExecutorAuthentication(IOptions<GuildCaptureOptions> options, IOptions<TlsOptions> tls, OutpostFeature? feature = null)
{
    public const string ServicePrefix = "/rimlink.v1.BattleExecutor/";
    public const string ItemKey = "rimlink.executor";
    private const string GenerationKey="rimlink.executor.credential-generation";
    internal void Authenticate(ServerCallContext context)
    {
        var config = options.Value;
        RequireTransport(config, feature);
        if (tls.Value.Required && !context.GetHttpContext().Request.IsHttps) throw Refused();
        var headers = context.RequestHeaders.Where(h => h.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)).ToArray();
        const string prefix = "RimlinkExecutor ";
        if (headers.Length != 1 || headers[0].IsBinary || !headers[0].Value.StartsWith(prefix, StringComparison.Ordinal)
            || headers[0].Value.Length - prefix.Length is < 32 or > 256) throw Refused();
        byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(headers[0].Value[prefix.Length..]));
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(config.ExecutorTokenSha256))) throw Refused();
        context.GetHttpContext().Items[ItemKey] = config.ExecutorId;
        context.GetHttpContext().Items[GenerationKey]=config.ExecutorTokenSha256;
    }
    internal static void RequireTransport(GuildCaptureOptions config, OutpostFeature? feature)
    {
        if (!GuildCaptureOptions.Configured(config))
            throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"executor transport is not configured");
        if (config.Enabled) return;
        try
        {
            if (feature is not { RuntimeComposition: true } || feature.GuildGrant is null)
                throw new OutpostEvidenceUnavailableException("executor-transport-disabled");
            feature.RequireGuildReadiness(GuildRuntimeUse.Transport);
        }
        catch (OutpostEvidenceUnavailableException)
        {
            // A writer withdraws the cut while holding S; this is not a terminal refusal of the retained executor.
            bool publishing=feature is{RuntimeComposition:true,GuildCut:null} && feature.GuildGrant is{ClockSynchronized:true}
                && feature.Readiness is{Mode:OutpostRuntimeMode.IsolatedAcceptance,DomainFormat:OutpostDomainFormat.V3,CurrentSchemaVerified:true,
                    CanResolveCustody:true,CanRunDomainWriters:true,Startup.State:OutpostStartupState.Ready};
            throw RpcErrors.With(publishing?StatusCode.Unavailable:StatusCode.FailedPrecondition,ErrorCode.GuildUnavailable,
                RefusalKeys.GuildUnavailable,"executor transport is not admitted");
        }
    }
    internal static string Identity(ServerCallContext context) => context.GetHttpContext().Items[ItemKey] as string ?? throw Refused();
    internal static string Generation(ServerCallContext context)=>context.GetHttpContext().Items[GenerationKey] as string??throw Refused();
    private static RpcException Refused() => RpcErrors.With(StatusCode.Unauthenticated, ErrorCode.BattleExecutorRefused,
        RefusalKeys.GuildExecutor, "dedicated executor credential required");
}
