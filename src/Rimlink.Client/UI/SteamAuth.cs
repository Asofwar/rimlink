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
    private static readonly TimeSpan TicketConfirmation = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The ticket of this game, across sessions: replaced by the next login, never cancelled when a session ends. After such a cancel
    /// Steam refused new tickets for about two minutes (2026-10-30). Steam drops it when the game exits.
    /// </summary>
    private static HAuthTicket ticket = HAuthTicket.Invalid;

    private readonly PresetLogin? preset;
    private PasswordLogin? password;

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

        if (!PreferPassword && SteamManager.Initialized && await GetTicket(cancellationToken) is { } steamTicket)
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

    /// <summary>
    /// A new ticket, once Steam confirmed it (the callback arrives on the main thread, where <see cref="SteamManager"/> runs it).
    /// No confirmation in time: the ticket goes anyway, and a refusal is retried by the login flow.
    /// </summary>
    private static async Task<byte[]?> GetTicket(CancellationToken cancellationToken)
    {
        var confirmed = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Callback<GetAuthSessionTicketResponse_t>? callback = null;
        byte[]? bytes = await MainThread.Get(() =>
        {
            try
            {
                if (ticket != HAuthTicket.Invalid) SteamUser.CancelAuthTicket(ticket);
                var issued = HAuthTicket.Invalid;
                callback = Callback<GetAuthSessionTicketResponse_t>.Create(r => { if (r.m_hAuthTicket == issued) confirmed.TrySetResult(r.m_eResult); });
                byte[] buffer = new byte[1024];
                issued = ticket = SteamUser.GetAuthSessionTicket(buffer, buffer.Length, out uint length);
                return ticket == HAuthTicket.Invalid || length == 0 ? null : buffer[..(int)length];
            }
            catch (Exception ex)
            {
                Log.Warning("[Rimlink] Steam ticket unavailable: " + ex.Message);
                return null;
            }
        });
        try
        {
            if (bytes is null) return null;
            await Task.WhenAny(confirmed.Task, Task.Delay(TicketConfirmation, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!confirmed.Task.IsCompleted) Log.Warning("[Rimlink] Steam did not confirm the ticket in time, sending it anyway");
            else if (confirmed.Task.Result != EResult.k_EResultOK) Log.Warning("[Rimlink] Steam answered the ticket with " + confirmed.Task.Result);
            return bytes;
        }
        finally
        {
            MainThread.Post(() => callback?.Dispose());
        }
    }

    /// <summary>The ticket stays alive after the session: see <see cref="ticket"/>.</summary>
    public void Dispose() { }
}
