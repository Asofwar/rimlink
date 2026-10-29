using System.Security.Cryptography;
using System.Text;

namespace Rimlink.Server.Rest;

public static class AdminToken
{
    /// <summary>Constant-time comparison of a presented token (trimmed) with a configured one; a blank configured token never matches.</summary>
    public static bool Matches(string? expected, string? given)
    {
        if (string.IsNullOrWhiteSpace(expected) || given is null) return false;
        byte[] want = Encoding.UTF8.GetBytes(expected);
        byte[] got = Encoding.UTF8.GetBytes(given.Trim());
        return got.Length == want.Length && CryptographicOperations.FixedTimeEquals(got, want);
    }
}
