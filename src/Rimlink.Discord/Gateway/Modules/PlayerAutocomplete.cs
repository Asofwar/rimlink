using Discord;
using Discord.Interactions;
using Rimlink.Discord.Api;

namespace Rimlink.Discord.Gateway.Modules;

/// <summary>Player names for a <c>player</c> or <c>seller</c> option, from the server's player search.</summary>
public sealed class PlayerAutocomplete(RimlinkApi api) : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter, IServiceProvider services)
    {
        string typed = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";
        try
        {
            return AutocompletionResult.FromSuccess((await Suggestions(api, typed, CancellationToken.None)).Select(n => new AutocompleteResult(n, n)));
        }
        catch (RimlinkApiException)
        {
            return AutocompletionResult.FromSuccess();
        }
    }

    /// <summary>
    /// Names containing what was typed. The bot's token searches logins, SteamIDs and <c>#id</c>s too, and any member may type
    /// here: a match only by those must not reveal whose they are.
    /// </summary>
    public static async Task<List<string>> Suggestions(RimlinkApi api, string typed, CancellationToken ct)
    {
        var page = await api.Players(typed, 25, ct);
        return page.Items.Where(p => p.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).ToList();
    }
}
