using Google.Protobuf;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Steamworks;
using Verse;
using Verse.Steam;

namespace Rimlink.Client.UI;

/// <summary>Builds login requests: a Steam session ticket when Steam runs, otherwise a login/password asked from the player.</summary>
public sealed class GameCredentials : ICredentialProvider
{
    private readonly PresetLogin? preset;
    private PasswordLogin? password;
    private HAuthTicket ticket = HAuthTicket.Invalid;

    public GameCredentials() { }

    /// <summary>Unattended (dev tooling): no Steam, no dialogs; the attempts come from <paramref name="preset"/>.</summary>
    public GameCredentials(PresetLogin preset)
    {
        this.preset = preset;
        PreferPassword = true;
        ServerPassword = preset.ServerPassword;
    }

    /// <summary>False for a preset login: nothing may open a dialog to ask the player.</summary>
    public bool Interactive => preset is null;

    public string? ServerPassword { get; set; }

    /// <summary>The login name the password dialog starts with (rejoin after a restart: the password is never kept).</summary>
    public string? PrefillLogin { get; set; }
    public bool PreferPassword { get; set; }
    public PasswordLogin? LastPassword => password;

    /// <summary>Set once a login failed, so the next prompt shows why.</summary>
    public string? LastError { get; set; }

    public async Task<LoginRequest> BuildLogin(CancellationToken cancellationToken)
    {
        var request = new LoginRequest { ServerPassword = ServerPassword ?? "" };
        request.Mods.AddRange(await ModpackActions.RunningMods());
        request.ModSettings.AddRange(ModpackActions.RunningModSettings());

        if (!PreferPassword && SteamManager.Initialized && TryGetTicket(out byte[] steamTicket))
        {
            request.SteamTicket = ByteString.CopyFrom(steamTicket);
            return request;
        }

        // Reconnects reuse the password typed before; the first login asks (a preset hands out its next attempt).
        if (preset is not null) password ??= preset.Next() is { } a ? new PasswordLogin(a.Login, a.Password, a.Register, a.PlayerName) : null;
        else password ??= await MainThread.Ask<PasswordLogin?>(tcs => new Dialog_Login(tcs, LastError, PrefillLogin));
        if (password is null) throw new OperationCanceledException("login cancelled");
        request.Password = new PasswordCredential { Login = password.Login, Password = password.Password };
        return request;
    }

    public RegisterRequest? TakeRegistration()
    {
        if (password is not { Register: true } p) return null;
        password = p with { Register = false };
        return new RegisterRequest { Login = p.Login, Password = p.Password, PlayerName = p.PlayerName };
    }

    public void ForgetPassword() => password = null;

    private bool TryGetTicket(out byte[] bytes)
    {
        bytes = [];
        try
        {
            if (ticket != HAuthTicket.Invalid) SteamUser.CancelAuthTicket(ticket);
            byte[] buffer = new byte[1024];
            ticket = SteamUser.GetAuthSessionTicket(buffer, buffer.Length, out uint length);
            if (ticket == HAuthTicket.Invalid || length == 0) return false;
            bytes = buffer[..(int)length];
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("[Rimlink] Steam ticket unavailable: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (ticket != HAuthTicket.Invalid) { try { SteamUser.CancelAuthTicket(ticket); } catch { } }
        ticket = HAuthTicket.Invalid;
    }
}
