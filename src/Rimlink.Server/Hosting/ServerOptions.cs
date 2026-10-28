namespace Rimlink.Server.Hosting;

public sealed class ServerOptions
{
    public const string Section = "Server";

    public int Port { get; set; } = 25560;
    public string DataDir { get; set; } = "./data";
    public int MaxPlayers { get; set; } = 50;
    public string? ServerPassword { get; set; }
    public int MaxSettlementsPerPlayer { get; set; } = 1;
    public int ProtocolVersion => Protocol.ProtocolVersion.Current;
}

public sealed class TlsOptions
{
    public const string Section = "Tls";

    public bool Required { get; set; } = true;
    /// <summary>PEM certificate; used together with <see cref="KeyPath"/>. Empty = self-signed.</summary>
    public string? CertificatePath { get; set; }
    public string? KeyPath { get; set; }
    /// <summary>Where the generated self-signed certificate lives; default &lt;DataDir&gt;/tls/server.pfx.</summary>
    public string? SelfSignedPath { get; set; }
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    public bool AllowPassword { get; set; }
    public string? SteamWebApiKey { get; set; }
    public long[] AdminSteamIds { get; set; } = [];
    public string[] AdminLogins { get; set; } = [];
}

public sealed class AdminOptions
{
    public const string Section = "Admin";

    public string? ApiToken { get; set; }
}
