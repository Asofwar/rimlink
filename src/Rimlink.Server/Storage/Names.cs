using System.Text.RegularExpressions;
using Rimlink.Protocol;

namespace Rimlink.Server.Storage;

/// <summary>Server-side check for player, faction, settlement and guild names. Names are never used as file paths.</summary>
public static partial class Names
{
    private static readonly Regex Pattern = new(Limits.NameRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValid(string? name) =>
        name is not null && !name.Any(char.IsControl) && Pattern.IsMatch(name);

    /// <summary>A valid name made from free text (a Steam name): characters a name cannot hold are dropped; null when too little is left.</summary>
    public static string? Clean(string? text)
    {
        if (text is null) return null;
        var kept = text.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or '\'').ToArray();
        string name = string.Join(' ', new string(kept).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > Limits.NameMaxLength) name = name[..Limits.NameMaxLength].TrimEnd();
        return IsValid(name) ? name : null;
    }
}
