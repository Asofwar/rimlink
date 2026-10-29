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
            var page = await api.Players(typed, 25);
            return AutocompletionResult.FromSuccess(page.Items.Select(p => new AutocompleteResult(p.Name, p.Name)));
        }
        catch (RimlinkApiException)
        {
            return AutocompletionResult.FromSuccess();
        }
    }
}
