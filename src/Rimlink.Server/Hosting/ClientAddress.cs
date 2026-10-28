using System.Net;

namespace Rimlink.Server.Hosting;

/// <summary>One textual form of a client address: IPv4-mapped IPv6 ("::ffff:1.2.3.4" on a dual-stack listener) becomes "1.2.3.4".</summary>
public static class ClientAddress
{
    public const string Unknown = "unknown";

    public static string Normalize(IPAddress? address)
    {
        if (address is null) return Unknown;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    public static string Normalize(EndPoint? endpoint) => endpoint is IPEndPoint ip ? Normalize(ip.Address) : Unknown;
}
