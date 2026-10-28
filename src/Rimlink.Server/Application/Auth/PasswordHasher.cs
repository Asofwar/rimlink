using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Rimlink.Server.Application.Auth;

/// <summary>Argon2id, 64 MB, 3 iterations, 2 lanes, 16-byte salt, 32-byte hash. Stored as "argon2id$salt$hash" (base64).</summary>
public static class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MemoryKb = 64 * 1024;
    private const int Iterations = 3;
    private const int Lanes = 2;

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(password, salt);
        return $"argon2id${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(password)) return false;

        string[] parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "argon2id") return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Derive(password, salt), expected);
    }

    private static byte[] Derive(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = MemoryKb,
            Iterations = Iterations,
            DegreeOfParallelism = Lanes,
        };
        return argon2.GetBytes(HashBytes);
    }
}
