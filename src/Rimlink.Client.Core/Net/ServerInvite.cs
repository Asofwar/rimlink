using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Rimlink.Client.Net;

/// <summary>A local-only, canonical address and certificate pin. Parsing never resolves hosts or opens links.</summary>
public sealed record ServerInvite
{
    public const int MaxLength = 512;
    public const int DefaultPort = 25560;
    public ServerEndpoint Endpoint { get; }
    public string Fingerprint { get; }
    public string Text => $"rimlink://{Endpoint.Host}:{Endpoint.Port}?fp={Fingerprint}";

    private ServerInvite(ServerEndpoint endpoint, string fingerprint) => (Endpoint, Fingerprint) = (endpoint, fingerprint);

    public static bool TryParse(string? text, out ServerInvite? invite)
    {
        invite = null;
        if (text is null || text.Length > MaxLength || !text.StartsWith("rimlink://", StringComparison.Ordinal)) return false;
        int query = text.IndexOf("?fp=", StringComparison.Ordinal);
        if (query < 10 || text.Length - query - 4 != 64) return false;
        string hash = text[(query + 4)..];
        if (!hash.All(char.IsAsciiHexDigit)) return false;
        string authority = text[10..query];
        string host;
        string? portText = null;
        if (authority.StartsWith('['))
        {
            int end = authority.IndexOf(']');
            if (end < 0) return false;
            string literal = authority[1..end];
            if (literal.Contains('%') || !IPAddress.TryParse(literal, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
            host = $"[{ip}]";
            if (end + 1 < authority.Length)
            {
                if (authority[end + 1] != ':') return false;
                portText = authority[(end + 2)..];
            }
        }
        else
        {
            int colon = authority.IndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            if (colon >= 0) portText = authority[(colon + 1)..];
            if (!ValidHost(host)) return false;
            host = host.ToLowerInvariant();
        }
        int port = DefaultPort;
        if (portText is not null && (portText.Length is < 1 or > 5 || !portText.All(char.IsAsciiDigit) ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) return false;
        invite = new ServerInvite(new ServerEndpoint(host, port), hash.ToUpperInvariant());
        return true;
    }

    private static bool ValidHost(string host)
    {
        if (host.Length is < 1 or > 253) return false;
        if (IPAddress.TryParse(host, out var address) && address.ToString() != host) return false;
        if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            var parts = host.Split('.');
            return parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && (p.Length == 1 || p[0] != '0') && byte.TryParse(p, out _));
        }
        return host.Split('.').All(p => p.Length is >= 1 and <= 63 && char.IsAsciiLetterOrDigit(p[0]) &&
            char.IsAsciiLetterOrDigit(p[^1]) && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
