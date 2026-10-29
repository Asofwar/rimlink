using Rimlink.Client.Net;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class PresetLoginTests
{
    private static readonly ServerEndpoint Server = new("127.0.0.1", 25560, "local");

    [Fact]
    public void FirstAttemptIsALogin()
    {
        var preset = new PresetLogin("nova", "pw", "Nova", registerIfRefused: true, serverPassword: null);
        Assert.Equal(new LoginAttempt("nova", "pw", false, "Nova"), preset.Next());
    }

    [Fact]
    public void RegisterIsTriedOnceAfterARefusedLogin()
    {
        // The server answers an unknown login and a wrong password alike: registering is the second and last try.
        var preset = new PresetLogin("nova", "pw", "Nova", registerIfRefused: true, serverPassword: null);
        Assert.False(preset.Next()!.Register);
        Assert.Equal(new LoginAttempt("nova", "pw", true, "Nova"), preset.Next());
        Assert.Null(preset.Next());
        Assert.Null(preset.Next());
        Assert.Equal(2, preset.Attempts);
    }

    [Fact]
    public void NoRegisterWhenNotAllowed()
    {
        var preset = new PresetLogin("nova", "pw", "Nova", registerIfRefused: false, serverPassword: null);
        Assert.NotNull(preset.Next());
        Assert.Null(preset.Next());
        Assert.Equal(1, preset.Attempts);
    }

    [Fact]
    public void BlankPlayerNameFallsBackToLogin()
    {
        var preset = new PresetLogin("nova", "pw", "  ", registerIfRefused: true, serverPassword: null);
        Assert.Equal("nova", preset.Next()!.PlayerName);
    }

    [Fact]
    public void ServerPasswordIsPassedThrough()
    {
        var preset = new PresetLogin("nova", "pw", null, registerIfRefused: true, serverPassword: "join");
        Assert.Equal("join", preset.ServerPassword);
        Assert.Equal("nova", preset.Login);
    }

    [Fact]
    public void AttemptNeverPrintsThePassword()
    {
        var preset = new PresetLogin("nova", "s3cret-pass", "Nova", registerIfRefused: true, serverPassword: null);
        Assert.DoesNotContain("s3cret-pass", preset.Next()!.ToString());
    }

    [Fact]
    public async Task PinPromptTrustsNewAndChangedOnlyWhenAllowed()
    {
        var strict = new PresetPinPrompt(trustChanged: false);
        Assert.True(await strict.Confirm(Server, "aa", previous: null));
        Assert.Equal("trusted new certificate", strict.Decision);
        Assert.False(await strict.Confirm(Server, "aa", previous: "bb"));
        Assert.Equal("refused changed certificate", strict.Decision);

        var lenient = new PresetPinPrompt(trustChanged: true);
        Assert.True(await lenient.Confirm(Server, "aa", previous: "bb"));
        Assert.Equal("trusted changed certificate", lenient.Decision);
    }
}
