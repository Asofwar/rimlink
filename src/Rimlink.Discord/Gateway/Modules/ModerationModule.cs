using Discord.Interactions;
using Rimlink.Discord.Commands;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Gateway.Modules;

/// <summary>Moderator commands. Visible to every member; the level check in the handlers refuses the others.</summary>
public sealed class ModerationModule(CommandHandlers h, Permissions permissions, Strings s) : RimlinkModule(permissions, s)
{
    [SlashCommand("kick", "Disconnect a player")]
    public Task Kick([Summary("player", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string player, [Summary("reason", "Shown to the player")] string? reason = null) =>
        Answer(c => h.Kick(c, player, reason));

    [SlashCommand("mute", "Mute a player in the game chat")]
    public Task Mute([Summary("player", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string player, [Summary("minutes", "How long"), MinValue(1)] int minutes) =>
        Answer(c => h.Mute(c, player, minutes));

    [SlashCommand("unmute", "Lift a chat mute")]
    public Task Unmute([Summary("player", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string player) => Answer(c => h.Unmute(c, player));

    [SlashCommand("broadcast", "Announce to everyone in the game")]
    public Task Broadcast([Summary("text", "The announcement")] string text) => Answer(c => h.Broadcast(c, text));

    [SlashCommand("bans", "Active bans")]
    public Task Bans() => Answer(c => h.Bans(c));

    [SlashCommand("actions", "The latest entries of the admin audit log")]
    public Task Actions([Summary("limit", "How many (1-50)"), MinValue(1), MaxValue(50)] int limit = 20) => Answer(c => h.Actions(c, limit));
}
