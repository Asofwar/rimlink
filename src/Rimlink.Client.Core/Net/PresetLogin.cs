namespace Rimlink.Client.Net;

/// <summary>One login or registration try of an unattended login.</summary>
public sealed record LoginAttempt(string Login, string Password, bool Register, string PlayerName)
{
    /// <summary>Never prints the password (records print every member by default; this one can reach a log).</summary>
    public override string ToString() => $"LoginAttempt {{ Login = {Login}, Register = {Register}, PlayerName = {PlayerName} }}";
}

/// <summary>
/// Credentials known up front (dev tooling, no dialogs): a login first, then, if that is refused and allowed, one
/// registration, then nothing. The server answers an unknown login and a wrong password alike, so registering can only
/// follow a refused login; a refused registration ("login already taken") ends the attempts instead of looping.
/// </summary>
public sealed class PresetLogin(string login, string password, string? playerName, bool registerIfRefused, string? serverPassword)
{
    private readonly string name = string.IsNullOrWhiteSpace(playerName) ? login : playerName.Trim();

    public string Login { get; } = login;
    public string? ServerPassword { get; } = serverPassword;

    /// <summary>How many attempts <see cref="Next"/> has handed out.</summary>
    public int Attempts { get; private set; }

    public LoginAttempt? Next()
    {
        if (Attempts >= (registerIfRefused ? 2 : 1)) return null;
        Attempts++;
        return new LoginAttempt(Login, password, Register: Attempts == 2, name);
    }
}

/// <summary>Answers the certificate question without asking: a first contact is trusted, a changed certificate only when allowed.</summary>
public sealed class PresetPinPrompt(bool trustChanged) : IPinPrompt
{
    /// <summary>What the last question got, for the caller's report.</summary>
    public string? Decision { get; private set; }

    public Task<bool> Confirm(ServerEndpoint endpoint, string fingerprint, string? previous)
    {
        bool trust = previous is null || trustChanged;
        Decision = previous is null ? "trusted new certificate" : trust ? "trusted changed certificate" : "refused changed certificate";
        return Task.FromResult(trust);
    }
}
