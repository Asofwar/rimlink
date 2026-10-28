namespace Rimlink.Client.Net;

/// <summary>Back-off after a dropped connection: 1, 2, 4, 8, 16, 32 s then the rest of a 2-minute budget.</summary>
public static class ReconnectPolicy
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public static IEnumerable<TimeSpan> Delays()
    {
        var spent = TimeSpan.Zero;
        var next = TimeSpan.FromSeconds(1);
        while (spent < Deadline)
        {
            var delay = next > MaxDelay ? MaxDelay : next;
            if (spent + delay > Deadline) delay = Deadline - spent;
            yield return delay;
            spent += delay;
            next += next;
        }
    }
}
