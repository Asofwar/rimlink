using Microsoft.Extensions.Time.Testing;
using Rimlink.Discord.Commands;
using Rimlink.Discord.Text;
using Xunit;

namespace Rimlink.Discord.Tests.Commands;

public class ConfirmationsTests
{
    private static readonly Strings S = new("en");
    private static readonly Caller Owner = new(9, "Boss", Level.Admin);

    private readonly FakeTimeProvider time = new();
    private readonly Confirmations confirmations;
    private int runs;

    public ConfirmationsTests() => confirmations = new Confirmations(time, S);

    private Confirmation Create() => confirmations.Create(Owner, "Reset Nova?", async () =>
    {
        await Task.Yield();
        Interlocked.Increment(ref runs);
        return "ran";
    });

    [Fact]
    public async Task ConfirmationRunsOnceForItsOwner()
    {
        var c = Create();

        var stranger = await confirmations.Press(c.Id, 5, confirm: true);
        Assert.Equal(S.T("cmd.confirm.notyours"), stranger.Text);
        Assert.True(stranger.Ephemeral);
        Assert.Equal(0, runs);

        Assert.Equal("ran", (await confirmations.Press(c.Id, Owner.UserId, confirm: true)).Text);
        Assert.Equal(S.T("cmd.confirm.expired"), (await confirmations.Press(c.Id, Owner.UserId, confirm: true)).Text);
        Assert.Equal(1, runs);

        var twice = Create();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => confirmations.Press(twice.Id, Owner.UserId, confirm: true))));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task ConfirmationExpires()
    {
        var c = Create();
        time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(S.T("cmd.confirm.expired"), (await confirmations.Press(c.Id, Owner.UserId, confirm: true)).Text);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task CancelDoesNotRun()
    {
        var c = Create();

        Assert.Equal(S.T("cmd.confirm.cancelled"), (await confirmations.Press(c.Id, Owner.UserId, confirm: false)).Text);
        Assert.Equal(S.T("cmd.confirm.expired"), (await confirmations.Press(c.Id, Owner.UserId, confirm: true)).Text);
        Assert.Equal(0, runs);
    }
}
