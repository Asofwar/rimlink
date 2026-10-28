using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Saves;

public class SaveRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RetentionKeepsTenLatestAndSevenDaily()
    {
        // Two versions per day for 20 days, newest = version 40 today.
        var all = Enumerable.Range(1, 40).Select(n => new SaveVersion
        {
            Number = n,
            CreatedAt = Now.AddDays(-(40 - n) / 2).AddHours((n % 2 == 0) ? 0 : -6),
            IsCurrent = n == 40,
        }).ToList();

        var toDelete = SaveRetention.Select(all, Now).Select(v => v.Number).ToHashSet();
        var kept = all.Where(v => !toDelete.Contains(v.Number)).Select(v => v.Number).Order().ToList();

        // Ten latest: 31..40. Daily for the last 7 days (today .. 6 days ago) = latest of each day: 40, 38, 36, 34, 32, 30, 28.
        Assert.Equal([28, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40], kept);
    }

    [Fact]
    public void CurrentVersionIsNeverDeleted()
    {
        var all = Enumerable.Range(1, 30).Select(n => new SaveVersion { Number = n, CreatedAt = Now.AddDays(-30 + n), IsCurrent = n == 1 }).ToList();

        var toDelete = SaveRetention.Select(all, Now);

        Assert.DoesNotContain(toDelete, v => v.Number == 1);
    }

    [Fact]
    public void FewVersionsAreAllKept()
    {
        var all = Enumerable.Range(1, 5).Select(n => new SaveVersion { Number = n, CreatedAt = Now.AddDays(-20 + n) }).ToList();

        Assert.Empty(SaveRetention.Select(all, Now));
    }
}
