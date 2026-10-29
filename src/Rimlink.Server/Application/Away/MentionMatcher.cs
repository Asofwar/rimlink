namespace Rimlink.Server.Application.Away;

/// <summary>Whether a chat line names a player: the name as a whole word, any case, taken literally.</summary>
public static class MentionMatcher
{
    public static bool Mentions(string text, string name)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name)) return false;
        for (int at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            bool startsWord = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            int end = at + name.Length;
            bool endsWord = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (startsWord && endsWord) return true;
        }
        return false;
    }
}
