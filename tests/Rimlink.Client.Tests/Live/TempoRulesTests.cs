using Rimlink.Client.Live;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Live;

/// <summary>Live UX spec §4 and the live spec's tempo rules: the slower wish, pauses, the guest's budget, the letter pause.</summary>
public class TempoRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A session the guest has joined (its Hello came): the waiting pause is off.</summary>
    private static TempoRules Joined(LetterPause host = LetterPause.Never, LetterPause guest = LetterPause.Never)
    {
        var rules = new TempoRules { HostThreshold = host, GuestThreshold = guest };
        rules.Begin();
        rules.WaitingPause = false;
        return rules;
    }

    [Fact]
    public void EffectiveIsTheSlowerWish()
    {
        var rules = Joined();
        rules.SetHostWish(3);
        rules.SetGuestSpeed(1, Now);

        Assert.Equal(1, rules.Effective);
        rules.ToggleHostPause();
        Assert.Equal((0, 1), (rules.Effective, rules.PausedBy));
    }

    [Fact]
    public void StricterThresholdDecides()
    {
        var rules = Joined(host: LetterPause.Never, guest: LetterPause.Major);

        Assert.False(rules.OnThreatLetter(major: false, "Manhunter"));
        Assert.True(rules.OnThreatLetter(major: true, "Raid"));
        Assert.Equal((3, "Raid", 0), (rules.PausedBy, rules.LetterLabel, rules.Effective));

        var anyThreat = Joined(host: LetterPause.Any, guest: LetterPause.Never);
        Assert.True(anyThreat.OnThreatLetter(major: false, "Manhunter"));
    }

    [Fact]
    public void ManyLettersOnePause()
    {
        var rules = Joined(host: LetterPause.Any);
        rules.SetHostWish(2);
        rules.SetGuestSpeed(3, Now);

        rules.OnThreatLetter(major: true, "Raid");
        rules.OnThreatLetter(major: false, "Manhunter");
        rules.OnThreatLetter(major: true, "Siege");
        rules.ToggleHostPause();

        Assert.False(rules.LetterPaused);
        Assert.Equal((2, 0), (rules.Effective, rules.PausedBy));
    }

    [Fact]
    public void GuestSpeedLiftsALetterPauseFree()
    {
        var rules = Joined(guest: LetterPause.Major);
        rules.OnThreatLetter(major: true, "Raid");

        Assert.Null(rules.SetGuestSpeed(2, Now));

        Assert.False(rules.LetterPaused);
        Assert.Equal(0, rules.GuestPausesUsed);
        Assert.Equal(1, rules.Effective);
    }

    [Fact]
    public void GuestPauseDuringLetterPauseIsANoOp()
    {
        var rules = Joined(host: LetterPause.Major);
        rules.OnThreatLetter(major: true, "Raid");

        Assert.Null(rules.SetGuestPause(true, Now));

        Assert.Equal((0, 3, false), (rules.GuestPausesUsed, rules.PausedBy, rules.GuestPaused));
    }

    [Fact]
    public void NoLetterPauseBeforeTheGuestArrives()
    {
        var rules = new TempoRules { HostThreshold = LetterPause.Any };
        rules.Begin();

        Assert.False(rules.OnThreatLetter(major: true, "Raid"));
        Assert.Equal(1, rules.PausedBy); // the waiting pause, the host's side
    }

    [Fact]
    public void BudgetAndExpiry()
    {
        var rules = Joined();
        for (int i = 0; i < 10; i++)
        {
            Assert.Null(rules.SetGuestPause(true, Now));
            Assert.Null(rules.SetGuestPause(false, Now));
        }

        Assert.Equal("pause budget spent (10 pauses)", rules.SetGuestPause(true, Now));

        var fresh = Joined();
        fresh.SetGuestPause(true, Now);
        fresh.Tick(Now + TimeSpan.FromSeconds(59));
        Assert.True(fresh.GuestPaused);
        fresh.Tick(Now + TimeSpan.FromSeconds(60));
        Assert.False(fresh.GuestPaused);
    }
}
