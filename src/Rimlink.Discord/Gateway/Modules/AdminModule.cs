using Discord;
using Discord.Interactions;
using Rimlink.Discord.Commands;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Gateway.Modules;

/// <summary>Admin commands; the last four only run after their confirm button.</summary>
[DefaultMemberPermissions(GuildPermission.ManageGuild)]
public sealed class AdminModule(CommandHandlers h, Permissions permissions, Strings s) : RimlinkModule(permissions, s)
{
    [SlashCommand("ban", "Ban a player")]
    public Task Ban([Summary("player", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string player,
        [Summary("days", "Leave out for a permanent ban"), MinValue(0)] double days = 0, [Summary("reason", "Why")] string? reason = null) =>
        Answer(c => h.Ban(c, player, days > 0 ? days : null, reason));

    [SlashCommand("pardon", "Lift a ban")]
    public Task Pardon([Summary("ban", "Ban number from /bans")] long ban) => Answer(c => h.Pardon(c, ban));

    [SlashCommand("wallet", "Add or take a player's silver")]
    public Task Wallet([Summary("player", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string player,
        [Summary("delta", "Silver to add (negative takes)")] long delta, [Summary("reason", "Why")] string? reason = null) =>
        Answer(c => h.Wallet(c, player, delta, reason));

    [SlashCommand("peaceful", "Switch a player's peaceful mode at once")]
    public Task Peaceful([Summary("player", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string player, [Summary("on", "Peaceful on or off")] bool on) =>
        Answer(c => h.Peaceful(c, player, on));

    [SlashCommand("saves", "A player's save versions")]
    public Task Saves([Summary("player", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string player) => Answer(c => h.Saves(c, player));

    [SlashCommand("shutdown", "Shut the server down (asks to confirm)")]
    public Task Shutdown([Summary("seconds", "Countdown"), MinValue(0)] int seconds, [Summary("reason", "Shown to the players")] string? reason = null) =>
        Answer(c => h.Shutdown(c, seconds, reason));

    [SlashCommand("rollback", "Roll a player back to a save version (asks to confirm)")]
    public Task Rollback([Summary("player", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string player, [Summary("version", "Version from /saves"), MinValue(1)] int version) =>
        Answer(c => h.Rollback(c, player, version));

    [SlashCommand("reset", "Reset a player's colony (asks to confirm)")]
    public Task Reset([Summary("player", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string player) => Answer(c => h.Reset(c, player));

    [SlashCommand("recreateworld", "Archive every colony and start a new world (asks to confirm)")]
    public Task RecreateWorld() => Answer(c => h.RecreateWorld(c));
}
