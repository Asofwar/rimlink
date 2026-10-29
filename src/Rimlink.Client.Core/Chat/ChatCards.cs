using System.Text.RegularExpressions;

namespace Rimlink.Client.Chat;

/// <summary>Where a card token sits in a message and which world tile it names.</summary>
public sealed record CardSpan(int Start, int Length, int Tile);

/// <summary>
/// Cards in chat (live UX spec §3): a world tile as the token <c>[[tile:N]]</c> in the message text, so the server stores and relays it
/// as plain text. Clients show a link; older clients and the Discord bot show the token.
/// </summary>
public static class ChatCards
{
    public const int MaxPerMessage = 3;

    private static readonly Regex TokenPattern = new(@"\[\[tile:(\d{1,9})\]\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Token(int tile) => "[[tile:" + tile + "]]";

    /// <summary>The first <see cref="MaxPerMessage"/> well-formed tokens in order; anything else stays text.</summary>
    public static IReadOnlyList<CardSpan> Parse(string text)
    {
        var spans = new List<CardSpan>();
        foreach (Match m in TokenPattern.Matches(text))
        {
            if (spans.Count == MaxPerMessage) break;
            if (int.TryParse(m.Groups[1].Value, out int tile)) spans.Add(new CardSpan(m.Index, m.Length, tile));
        }
        return spans;
    }
}
