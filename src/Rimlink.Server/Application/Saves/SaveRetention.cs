using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Saves;

/// <summary>Keep the 10 newest versions plus the newest version of each of the last 7 calendar days (UTC). The current version always stays.</summary>
public static class SaveRetention
{
    public const int KeepLatest = 10;
    public const int KeepDailyDays = 7;

    public static IReadOnlyList<SaveVersion> Select(IReadOnlyList<SaveVersion> all, DateTime now)
    {
        var keep = new HashSet<long>();
        foreach (var v in all.OrderByDescending(v => v.Number).Take(KeepLatest)) keep.Add(v.Number);

        var today = now.Date;
        foreach (var group in all.Where(v => v.CreatedAt.Date > today.AddDays(-KeepDailyDays)).GroupBy(v => v.CreatedAt.Date))
        {
            keep.Add(group.MaxBy(v => v.Number)!.Number);
        }

        return all.Where(v => !v.IsCurrent && !keep.Contains(v.Number)).ToList();
    }
}
