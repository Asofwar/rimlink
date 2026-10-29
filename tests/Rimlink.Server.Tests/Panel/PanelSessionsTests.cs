using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Panel;

public class PanelSessionsTests
{
    [Fact]
    public void FailuresFromIpsThatNeverCameBackAreForgotten()
    {
        // The login endpoint is public: one wrong token from each of many addresses must not grow memory forever.
        var clock = new ManualTimeProvider();
        var sessions = new PanelSessions(Options.Create(new PanelOptions()), clock);
        for (int i = 0; i < 1500; i++) sessions.RecordFailure($"10.0.{i / 256}.{i % 256}");

        clock.Advance(PanelSessions.FailureWindow + TimeSpan.FromSeconds(1));
        sessions.RecordFailure("192.0.2.1");

        Assert.True(sessions.FailureEntries <= 1, $"{sessions.FailureEntries} entries kept");
    }
}
