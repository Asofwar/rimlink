using Rimlink.Discord.Api;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Commands;

/// <summary>A typed player name → one player: the exact name (any case), else the only name with that prefix, else an error text.</summary>
public sealed class PlayerResolver(RimlinkApi api, Strings s)
{
    public const int Candidates = 5;
    public const int PageSize = 200;

    public async Task<(PlayerInfo? Player, string? Error)> Resolve(string name, CancellationToken ct)
    {
        name = name.Trim();
        var page = await api.Players(name, PageSize, ct);
        var items = page.Items;
        if (items.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } exact) return (exact, null);

        var prefixed = items.Where(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        // One prefix match on a partial page proves nothing: the next page may hold another (and exact names sort first).
        bool partial = page.Total > items.Count;
        return prefixed.Count switch
        {
            1 when !partial => (prefixed[0], null),
            0 => (null, s.T("cmd.player.unknown", Formatter.Escape(name))),
            _ => (null, s.T("cmd.player.ambiguous", Formatter.Escape(name), string.Join(", ", prefixed.Take(Candidates).Select(p => Formatter.Escape(p.Name))))),
        };
    }
}
