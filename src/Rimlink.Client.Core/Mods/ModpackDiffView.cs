using Rimlink.Protocol.V1;

namespace Rimlink.Client.Mods;

public enum DiffCategory { Missing, Version, Files, Extra, Order, Settings }

public sealed record DiffRow(string PackageId, string Detail, FixSide Side);

public sealed record DiffSection(DiffCategory Category, IReadOnlyList<DiffRow> Rows);

/// <summary>What the modpack window shows: the non-empty categories in a fixed order, one row per mod.</summary>
public static class ModpackDiffView
{
    public static IReadOnlyList<DiffSection> Sections(ModpackDiff diff)
    {
        var sections = new List<DiffSection>();
        void Add(DiffCategory category, IEnumerable<DiffRow> rows)
        {
            var list = rows.ToList();
            if (list.Count > 0) sections.Add(new DiffSection(category, list));
        }

        Add(DiffCategory.Missing, diff.Missing.Select(m => new DiffRow(m.PackageId, Source(m), FixSide.Player)));
        Add(DiffCategory.Version, diff.WrongVersion.Select(v => new DiffRow(v.PackageId, $"{v.ClientVersion} → {v.ServerVersion}", v.Side)));
        Add(DiffCategory.Files, diff.Files.Select(f => new DiffRow(f.PackageId, f.Version, FixSide.Player)));
        Add(DiffCategory.Extra, diff.Extra.Select(e => new DiffRow(e.PackageId, "", FixSide.Player)));
        if (diff.OrderDiffers) sections.Add(new DiffSection(DiffCategory.Order, []));
        Add(DiffCategory.Settings, diff.Configs.Select(c => new DiffRow(c.PackageId, c.Handle, FixSide.Player)));
        return sections;
    }

    private static string Source(ModpackEntry entry)
    {
        string source = entry.WorkshopId != 0 ? "(Steam)" : entry.Url;
        return string.Join(" ", new[] { entry.Version, source }.Where(s => s.Length > 0));
    }
}
