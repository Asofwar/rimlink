using Rimlink.Server.Application.GuildCapture;
namespace Rimlink.Server.Application.Outposts;
public sealed partial class OutpostAuthority
{
    internal IGuildAcceptanceClockControl? GuildClock=>feature.RuntimeComposition?feature.GuildClock:null;
    internal GuildRuntimeGrant? GuildClockGrant=>feature.RuntimeComposition?feature.GuildGrant:null;
}
