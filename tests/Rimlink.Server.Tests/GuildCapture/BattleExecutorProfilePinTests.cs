using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public class BattleExecutorProfilePinTests
{
    private static readonly byte[] First = Enumerable.Repeat((byte)1, 32).ToArray();
    private static readonly byte[] Second = Enumerable.Repeat((byte)2, 32).ToArray();

    [Fact]
    public void Without_a_configured_digest_the_legacy_preflight_admits_nobody()
    {
        // Its only check is the pin; an unpinned server leaves admission to the hello path, which reads the recorded approval.
        var registry = new BattleExecutorRegistry(TimeProvider.System, "arbiter", []);
        Assert.Throws<InvalidDataException>(() => registry.RegisterAuthenticated("arbiter", Second));
        Assert.Null(registry.Available());
    }

    [Fact]
    public void A_configured_digest_still_pins_one_profile()
    {
        var registry = new BattleExecutorRegistry(TimeProvider.System, "arbiter", First);
        Assert.Throws<InvalidDataException>(() => registry.RegisterAuthenticated("arbiter", Second));
        Assert.Equal(First, registry.RegisterAuthenticated("arbiter", First).ProfileDigest.ToByteArray());
        Assert.Throws<InvalidDataException>(() => new BattleExecutorRegistry(TimeProvider.System, "arbiter", new byte[5]));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("not-a-digest", false)]
    public void The_profile_digest_setting_is_optional_but_never_malformed(string digest, bool valid)
    {
        var options = new GuildCaptureOptions { Enabled = true, ExecutorId = "arbiter", ExecutorTokenSha256 = new string('c', 64), ApprovedProfileDigest = digest };
        Assert.Equal(valid, GuildCaptureOptions.Valid(options));
    }
}
