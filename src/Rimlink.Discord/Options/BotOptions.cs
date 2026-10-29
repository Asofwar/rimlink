namespace Rimlink.Discord.Options;

/// <summary>The bot's configuration (Discord bot spec §6): section <c>RimlinkBot</c>, environment <c>RimlinkBot__Discord__Token</c>.</summary>
public sealed class BotOptions
{
    public const string Section = "RimlinkBot";

    public ServerSection Server { get; set; } = new();
    public DiscordSection Discord { get; set; } = new();
    /// <summary>File keeping the last admin feed id across restarts (compose: on the bot's volume); empty = start with new events.</summary>
    public string? StateFile { get; set; }

    /// <summary>Problems that stop the bot at start; empty when the options are usable.</summary>
    public static List<string> Validate(BotOptions o)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.Server.ApiToken)) errors.Add("RimlinkBot:Server:ApiToken is required (the bot's token from the server's Admin:Tokens)");
        if (string.IsNullOrWhiteSpace(o.Discord.Token)) errors.Add("RimlinkBot:Discord:Token is required (the bot token from the Discord Developer Portal)");
        if (!Uri.TryCreate(o.Server.Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            errors.Add($"RimlinkBot:Server:Url must be an absolute http(s) address, not '{o.Server.Url}'");
        if (o.Discord.Language is not ("en" or "ru")) errors.Add($"RimlinkBot:Discord:Language must be en or ru, not '{o.Discord.Language}'");
        if (!string.IsNullOrEmpty(o.Server.CertificateSha256) && (o.Server.CertificateSha256.Length != 64 || !o.Server.CertificateSha256.All(Uri.IsHexDigit)))
            errors.Add("RimlinkBot:Server:CertificateSha256 must be 64 hex characters (the server's certificateFingerprint)");
        return errors;
    }
}

public sealed class ServerSection
{
    public string Url { get; set; } = "https://rimlink:25560";
    public string ApiToken { get; set; } = "";
    /// <summary>HEX(SHA256) of the server certificate; a match is trusted without the chain and name checks.</summary>
    public string? CertificateSha256 { get; set; }
    /// <summary>
    /// The name a real (CA-issued) certificate is issued for, when <see cref="Url"/> uses another one (compose: rimlink): the
    /// chain is still checked, the name against this one.
    /// </summary>
    public string? CertificateName { get; set; }
    /// <summary>Accept any certificate. Only inside the compose network.</summary>
    public bool Insecure { get; set; }
}

public sealed class DiscordSection
{
    public string Token { get; set; } = "";
    public ulong GuildId { get; set; }
    public string Language { get; set; } = "en";
    public ulong[] ModeratorRoleIds { get; set; } = [];
    public ulong[] AdminRoleIds { get; set; } = [];
    public ChannelsSection Channels { get; set; } = new();
}

/// <summary>Discord channel per event group; 0 turns the group off.</summary>
public sealed class ChannelsSection
{
    public ulong Server { get; set; }
    public ulong Raids { get; set; }
    public ulong Market { get; set; }
    public ulong Moderation { get; set; }
    public ulong Chat { get; set; }
}
