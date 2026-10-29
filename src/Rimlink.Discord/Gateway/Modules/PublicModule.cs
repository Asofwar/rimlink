using Discord.Interactions;
using Rimlink.Discord.Commands;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Gateway.Modules;

/// <summary>Queries for every member; answered in the channel.</summary>
public sealed class PublicModule(CommandHandlers h, Permissions permissions, Strings s) : RimlinkModule(permissions, s)
{
    [SlashCommand("online", "Who is playing right now")]
    public Task Online() => Answer(c => h.Online(c), publicQuery: true);

    [SlashCommand("status", "Server version, players online and paused features")]
    public Task Status() => Answer(c => h.Status(c), publicQuery: true);

    [SlashCommand("market", "Open market lots")]
    public Task Market([Summary("seller", "Only this player's lots"), Autocomplete(typeof(PlayerAutocomplete))] string? seller = null) =>
        Answer(c => h.Market(c, seller), publicQuery: true);

    [SlashCommand("player", "A player's faction, colonies and status")]
    public Task Player([Summary("name", "Player name"), Autocomplete(typeof(PlayerAutocomplete))] string name) =>
        Answer(c => h.Player(c, name), publicQuery: true);
}
