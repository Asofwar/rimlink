namespace Rimlink.BridgeTools;

/// <summary>The "def:count,def:count" argument of the item tools. Game-free: unit-tested by link.</summary>
public static class ItemSpec
{
    /// <summary>
    /// One entry per def (names compared case-insensitively, the first spelling kept, counts summed): picking stacks
    /// for the same def twice would take one stack twice and send more than the map holds.
    /// </summary>
    public static List<(string Def, int Count)> Parse(string csv, out List<string> problems)
    {
        problems = [];
        var items = new List<(string Def, int Count)>();
        foreach (string token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out int count) || count <= 0) { problems.Add($"'{token}' is not def:count"); continue; }
            int i = items.FindIndex(x => string.Equals(x.Def, parts[0], StringComparison.OrdinalIgnoreCase));
            if (i >= 0) items[i] = (items[i].Def, items[i].Count + count);
            else items.Add((parts[0], count));
        }
        return items;
    }
}
