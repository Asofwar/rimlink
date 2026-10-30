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
    private static byte[]? ticketBytes;
    private static DateTime ticketIssuedAt;
    private static TaskCompletionSource<EResult>? ticketConfirmation;
    private static Callback<GetAuthSessionTicketResponse_t>? confirmationCallback;
    private static volatile bool ticketSentConfirmed;

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
    /// A ticket for this login, once Steam confirmed it (the callback arrives on the main thread, where <see cref="SteamManager"/> runs it).
    /// No confirmation in time: the ticket goes anyway, and a refusal is retried by the login flow with the same ticket
    /// (<see cref="SteamTicketReuse"/>) so that Steam can finish confirming it.
    /// </summary>
    private static async Task<byte[]?> GetTicket(CancellationToken cancellationToken)
    {
        var (bytes, confirmed) = await MainThread.Get(CurrentOrNewTicket);
        if (bytes is null || confirmed is null) return null;

        await Task.WhenAny(confirmed, Task.Delay(TicketConfirmation, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed.IsCompleted) Log.Warning("[Rimlink] Steam did not confirm the ticket in time, sending it anyway");
        else if (confirmed.Result != EResult.k_EResultOK) Log.Warning("[Rimlink] Steam answered the ticket with " + confirmed.Result);
        else ticketSentConfirmed = true;
        return bytes;
    }

    /// <summary>Main thread: the Steam calls and the callback that confirms them run there.</summary>
    private static (byte[]? Bytes, Task<EResult>? Confirmed) CurrentOrNewTicket()
    {
        try
        {
            var held = ticketConfirmation?.Task;
            bool failed = held is { IsCompleted: true } && held.Result != EResult.k_EResultOK;
            if (ticketBytes is not null && held is not null && !failed
                && SteamTicketReuse.Keep(held.IsCompleted, DateTime.UtcNow - ticketIssuedAt, ticketSentConfirmed))
                return (ticketBytes, held);

            if (ticket != HAuthTicket.Invalid) SteamUser.CancelAuthTicket(ticket);
            confirmationCallback?.Dispose();
            var issued = HAuthTicket.Invalid;
            var confirmation = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            confirmationCallback = Callback<GetAuthSessionTicketResponse_t>.Create(r => { if (r.m_hAuthTicket == issued) confirmation.TrySetResult(r.m_eResult); });
            byte[] buffer = new byte[1024];
            issued = ticket = SteamUser.GetAuthSessionTicket(buffer, buffer.Length, out uint length);
            ticketConfirmation = confirmation;
            ticketIssuedAt = DateTime.UtcNow;
            ticketSentConfirmed = false;
            ticketBytes = ticket == HAuthTicket.Invalid || length == 0 ? null : buffer[..(int)length];
            return (ticketBytes, confirmation.Task);
        }
        catch (Exception ex)
        {
            Log.Warning("[Rimlink] Steam ticket unavailable: " + ex.Message);
            return (null, null);
        }
    }

    /// <summary>The ticket stays alive after the session: see <see cref="ticket"/>.</summary>
    public void Dispose() { }
}
