using System.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public sealed class GuildAcceptanceSelection
{
    public string StandId{get;}
    public string CertificateSha256{get;}
    public GuildAcceptanceSelection(string standId,string certificateSha256)
    {
        StandId=standId;CertificateSha256=certificateSha256;
        GuildRuntimeContracts.Require(Target);
        if(certificateSha256 is not{Length:64} || certificateSha256.Any(c=>!char.IsAsciiHexDigit(c)))throw Invalid();
    }
    public GuildRuntimeTarget Target=>new(){Mode=GuildRuntimeMode.IsolatedAcceptanceV1,StandId=StandId};
    public void RequireEndpoint(Uri endpoint)
    {
        if(endpoint.Scheme!="https" || !IPAddress.TryParse(endpoint.Host.Trim('[',']'),out var ip) || !IPAddress.IsLoopback(ip))throw Invalid();
    }
    public static void RequireReply(GuildAcceptanceSelection? selection,GuildRuntimeTarget? reply)
    {
        GuildRuntimeContracts.Require(reply);
        if(selection is null ? reply is not null : !selection.Target.Equals(reply))throw Invalid();
    }
    private static InvalidDataException Invalid()=>new("guild-isolated-stand-selection-mismatch");
}
