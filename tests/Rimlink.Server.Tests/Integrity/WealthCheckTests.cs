using Rimlink.Server.Application.Integrity;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests.Integrity;

public class WealthCheckTests
{
    private static readonly IntegrityOptions Defaults = new();

    private static WealthResult? Evaluate(long baseWealth, long baseTick, long newWealth, long newTick, long imported = 0, IntegrityOptions? options = null, bool isAdmin = false) =>
        WealthCheck.Evaluate(baseWealth, baseTick, newWealth, newTick, imported, options ?? Defaults, isAdmin);

    [Fact]
    public void RateIsNetGainPerDay()
    {
        var r = Evaluate(50000, 0, 110000, 120000, imported: 20000)!;

        Assert.Equal(40000, r.Net);
        Assert.Equal(2.0, r.Days);
        Assert.Equal(20000, r.Rate);
        Assert.Equal(WealthVerdict.Warn, r.Verdict);
    }

    [Fact]
    public void DaysAreAtLeastOne()
    {
        var r = Evaluate(50000, 1000, 80000, 2000)!;

        Assert.Equal(1.0, r.Days);
        Assert.Equal(30000, r.Rate);
    }

    [Fact]
    public void FreezeAtTheSecondThreshold()
    {
        Assert.Equal(WealthVerdict.Freeze, Evaluate(50000, 0, 150000, 60000)!.Verdict);
        Assert.Equal(WealthVerdict.Warn, Evaluate(50000, 0, 149999, 60000)!.Verdict);
        Assert.Equal(WealthVerdict.None, Evaluate(50000, 0, 69999, 60000)!.Verdict);
    }

    [Fact]
    public void AdminsAreOnlyWarned()
    {
        Assert.Equal(WealthVerdict.Warn, Evaluate(50000, 0, 550000, 60000, isAdmin: true)!.Verdict);
    }

    [Fact]
    public void ZeroThresholdsNeverFire()
    {
        var off = new IntegrityOptions { WealthWarnPerDay = 0, WealthFreezePerDay = 0 };
        var freezeOnly = new IntegrityOptions { WealthWarnPerDay = 0, WealthFreezePerDay = 100000 };

        Assert.Equal(WealthVerdict.None, Evaluate(50000, 0, 1_000_050_000, 60000, options: off)!.Verdict);
        Assert.Equal(WealthVerdict.Freeze, Evaluate(50000, 0, 200000, 60000, options: freezeOnly)!.Verdict);
        Assert.Equal(WealthVerdict.None, Evaluate(50000, 0, 120000, 60000, options: freezeOnly)!.Verdict);
    }

    [Fact]
    public void SkipsWhenTheBaseHasNoWealth() => Assert.Null(Evaluate(0, 0, 500000, 60000));

    [Fact]
    public void SkipsWhenTheTickDidNotAdvance()
    {
        Assert.Null(Evaluate(50000, 60000, 500000, 60000));
        Assert.Null(Evaluate(50000, 60000, 500000, 1000));
    }

    [Fact]
    public void ImportsLargerThanTheGainGiveANegativeNet()
    {
        var r = Evaluate(50000, 0, 60000, 60000, imported: 15000)!;

        Assert.Equal(-5000, r.Net);
        Assert.Equal(WealthVerdict.None, r.Verdict);
    }
}
