using System.Text.RegularExpressions;
using Rimlink.Protocol;

namespace Rimlink.Server.Storage;

/// <summary>Server-side check for player, faction, settlement and guild names. Names are never used as file paths.</summary>
public static partial class Names
{
    private static readonly Regex Pattern = new(Limits.NameRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValid(string? name) =>
        name is not null && !name.Any(char.IsControl) && Pattern.IsMatch(name);
}
