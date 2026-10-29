using Rimlink.Discord.Options;

namespace Rimlink.Discord.Commands;

/// <summary>What a Discord member may run (Discord bot spec §5). The server sees every bot call as the bot's token: this is the only check.</summary>
public enum Level { Everyone, Moderator, Admin }

/// <summary>The Discord member behind a command.</summary>
public sealed record Caller(ulong UserId, string UserName, Level Level)
{
    /// <summary>The server refuses an <c>X-Rimlink-Actor</c> label longer than this.</summary>
    public const int LabelMax = 64;

    /// <summary><c>discord:&lt;name&gt; (&lt;id&gt;)</c>, the name cut so the label fits <see cref="LabelMax"/>; sent as <c>X-Rimlink-Actor</c>.</summary>
    public string Actor
    {
        get
        {
            string suffix = $" ({UserId})";
            string name = new(UserName.Where(c => !char.IsControl(c)).ToArray());
            int room = LabelMax - "discord:".Length - suffix.Length;
            return $"discord:{(name.Length > room ? name[..room] : name)}{suffix}";
        }
    }
}

/// <summary>Discord roles → <see cref="Level"/>: any admin role is Admin (which includes Moderator), any moderator role is Moderator.</summary>
public sealed class Permissions(DiscordSection options)
{
    public Level LevelOf(IEnumerable<ulong> roleIds)
    {
        var roles = roleIds.ToHashSet();
        if (options.AdminRoleIds.Any(roles.Contains)) return Level.Admin;
        if (options.ModeratorRoleIds.Any(roles.Contains)) return Level.Moderator;
        return Level.Everyone;
    }
}

/// <summary>A command's answer; <see cref="Confirm"/> asks for the confirm/cancel buttons of an irreversible command.</summary>
public sealed record Reply(string Text, bool Ephemeral, Confirmation? Confirm = null);

public sealed record Confirmation(string Id, string Description);
