using Grpc.Core;
using Grpc.Net.Client;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Net;

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Replaced, Failed }

/// <summary>Supplies the credential part of a login (Steam ticket or password), the running mods and the server password.</summary>
public interface ICredentialProvider
{
    Task<LoginRequest> BuildLogin(CancellationToken cancellationToken);

    /// <summary>A new account to create instead of logging in; returned once, then null (reconnects log in).</summary>
    RegisterRequest? TakeRegistration() => null;
}

/// <summary>Asks the user whether to trust a server certificate that is new or has changed.</summary>
public interface IPinPrompt
{
    Task<bool> Confirm(ServerEndpoint endpoint, string fingerprint, string? previous);
}

/// <summary>
/// One session with one server: login, typed clients, the event stream and automatic reconnection.
/// Knows nothing about the game; events are raised on a network thread.
/// </summary>
public sealed class RimlinkConnection : IAsyncDisposable
{
    private readonly ServerEndpoint endpoint;
    private readonly ICredentialProvider credentials;
    private readonly IPinPrompt pinPrompt;
    private readonly ILog log;
    private readonly ConnectionTransport transport;
    private readonly GrpcChannel channel;
    private readonly NetworkDropHandler drop;
    private readonly ReconnectPolicy reconnect;
    private readonly CancellationTokenSource lifetime = new();
    private volatile string? token;
    private Task? eventLoop;
    private ConnectionState state = ConnectionState.Disconnected;
    private long reconnectingSinceTicks;
    private volatile bool pinRefused;
    private volatile bool steamLogin;

    public RimlinkConnection(ServerEndpoint endpoint, ICredentialProvider credentials, IPinPrompt pinPrompt, ILog log, ConnectionTransport? transport = null, ReconnectPolicy? reconnect = null)
    {
        this.reconnect = reconnect ?? ReconnectPolicy.Default;
        this.endpoint = endpoint;
        this.credentials = credentials;
        this.pinPrompt = pinPrompt;
        this.log = log;
        this.transport = transport ?? ConnectionTransport.Pinned(endpoint, Pins, check => pendingPin = check);

        var callCredentials = CallCredentials.FromInterceptor((_, metadata) =>
        {
            if (token is { } t) metadata.Add("authorization", $"Bearer {t}");
            return Task.CompletedTask;
        });
        bool insecure = this.transport.Address.Scheme == Uri.UriSchemeHttp;
        drop = new NetworkDropHandler(this.transport.Handler);
        channel = GrpcChannel.ForAddress(this.transport.Address, new GrpcChannelOptions
        {
            HttpHandler = drop,
            DisposeHttpClient = true,
            Credentials = ChannelCredentials.Create(insecure ? ChannelCredentials.Insecure : ChannelCredentials.SecureSsl, callCredentials),
            UnsafeUseInsecureChannelCallCredentials = insecure,
            MaxReceiveMessageSize = Protocol.Limits.MaxMessageBytes + 16 * 1024,
        });

        Auth = new Protocol.V1.Auth.AuthClient(channel);
        Events = new Protocol.V1.Events.EventsClient(channel);
        World = new Protocol.V1.World.WorldClient(channel);
        Saves = new Protocol.V1.Saves.SavesClient(channel);
        Chat = new Protocol.V1.Chat.ChatClient(channel);
        Modpack = new Protocol.V1.Modpack.ModpackClient(channel);
        Admin = new Protocol.V1.Admin.AdminClient(channel);
        WalletApi = new Protocol.V1.Wallet.WalletClient(channel);
        MailboxApi = new Protocol.V1.Mailbox.MailboxClient(channel);
        MarketApi = new Protocol.V1.Market.MarketClient(channel);
        TransferApi = new Protocol.V1.Transfer.TransferClient(channel);
        RaidsApi = new Protocol.V1.Raids.RaidsClient(channel);
        VisitsApi = new Protocol.V1.Visits.VisitsClient(channel);
        ShopApi = new Protocol.V1.Shop.ShopClient(channel);
        LiveApi = new Protocol.V1.Live.LiveClient(channel);
        TradesApi = new Protocol.V1.Trades.TradesClient(channel);
        SocialApi = new Protocol.V1.Social.SocialClient(channel);
    }

    /// <summary>Pins live next to the game's config; the game layer sets the path before the first connection.</summary>
    public static CertificatePinStore Pins { get; set; } = new(Path.Combine(AppContext.BaseDirectory, "rimlink-pins.json"));

    private PinCheck? pendingPin;

    /// <summary>
    /// Dev tooling only: this client loses its connection for <paramref name="duration"/> (open streams break, calls fail)
    /// and then reconnects the usual way. The machine's network is not touched.
    /// </summary>
    public void DropNetwork(TimeSpan duration) => drop.Drop(duration);

    public ServerEndpoint Endpoint => endpoint;
    public ConnectionState State => state;

    /// <summary>When the current outage began (UTC) while the connection is reconnecting; null otherwise.</summary>
    public DateTime? ReconnectingSince => Interlocked.Read(ref reconnectingSinceTicks) is var t and not 0 ? new DateTime(t, DateTimeKind.Utc) : null;

    /// <summary>Reconnecting for longer than the policy's offer mark: time to offer leaving to the menu. The attempts go on.</summary>
    public bool OutageIsLong => ReconnectingSince is { } since && DateTime.UtcNow - since >= reconnect.OfferExitAfter;

    /// <summary>Why the server refused the reconnect (state <see cref="ConnectionState.Failed"/>); null otherwise.</summary>
    public string? FailureReason { get; private set; }
    public LoginReply? Login { get; private set; }
    public event Action<ConnectionState>? StateChanged;
    public event Action<ServerEvent>? EventReceived;

    public Protocol.V1.Auth.AuthClient Auth { get; }
    public Protocol.V1.Events.EventsClient Events { get; }
    public Protocol.V1.World.WorldClient World { get; }
    public Protocol.V1.Saves.SavesClient Saves { get; }
    public Protocol.V1.Chat.ChatClient Chat { get; }
    public Protocol.V1.Modpack.ModpackClient Modpack { get; }
    public Protocol.V1.Admin.AdminClient Admin { get; }
    public Protocol.V1.Wallet.WalletClient WalletApi { get; }
    public Protocol.V1.Mailbox.MailboxClient MailboxApi { get; }
    public Protocol.V1.Market.MarketClient MarketApi { get; }
    public Protocol.V1.Transfer.TransferClient TransferApi { get; }
    public Protocol.V1.Raids.RaidsClient RaidsApi { get; }
    public Protocol.V1.Visits.VisitsClient VisitsApi { get; }
    public Protocol.V1.Shop.ShopClient ShopApi { get; }
    public Protocol.V1.Live.LiveClient LiveApi { get; }
    public Protocol.V1.Trades.TradesClient TradesApi { get; }
    public Protocol.V1.Social.SocialClient SocialApi { get; }

    /// <summary>Logs in and starts the event stream. A modpack mismatch returns the reply (no token) and leaves the connection down.</summary>
    public async Task<LoginReply> ConnectAsync(CancellationToken cancellationToken)
    {
        SetState(ConnectionState.Connecting);
        try
        {
            var reply = await LoginOnce(cancellationToken);
            if (reply.SessionToken.Length == 0)
            {
                SetState(ConnectionState.Disconnected);
                return reply;
            }
            StartEventLoop();
            SetState(ConnectionState.Connected);
            return reply;
        }
        catch
        {
            SetState(ConnectionState.Disconnected);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (token is { } t)
        {
            token = null;
            try { await Auth.LogoutAsync(new Empty(), new Metadata { { "authorization", $"Bearer {t}" } }); }
            catch (RpcException ex) { log.Warn($"logout failed: {ex.Status.StatusCode}"); }
        }
        lifetime.Cancel();
        if (eventLoop is { } loop) { try { await loop; } catch (OperationCanceledException) { } }
        SetState(ConnectionState.Disconnected);
    }

    private async Task<LoginReply> LoginOnce(CancellationToken cancellationToken)
    {
        var request = await credentials.BuildLogin(cancellationToken);
        request.ProtocolVersion = ClientInfo.ProtocolVersion;
        steamLogin = request.CredentialCase == LoginRequest.CredentialOneofCase.SteamTicket;
        var registration = credentials.TakeRegistration();
        if (registration is not null && registration.Mods.Count == 0) registration.Mods.AddRange(request.Mods);
        if (registration is not null && registration.ModSettings.Count == 0) registration.ModSettings.AddRange(request.ModSettings);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var reply = registration is not null
                    ? await Auth.RegisterAsync(registration, cancellationToken: cancellationToken)
                    : await Auth.LoginAsync(request, cancellationToken: cancellationToken);
                if (reply.SessionToken.Length > 0) token = reply.SessionToken;
                Login = reply;
                return reply;
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Internal && pendingPin is { } pin && attempt == 0)
            {
                // TLS handshake refused because the certificate is new or changed: ask, pin, retry once.
                pendingPin = null;
                if (!await pinPrompt.Confirm(endpoint, pin.Fingerprint, pin.Previous))
                {
                    pinRefused = true;
                    throw;
                }
                Pins.Save(endpoint, pin.Fingerprint);
            }
        }
    }

    private void StartEventLoop()
    {
        eventLoop = Task.Run(() => EventLoop(lifetime.Token));
    }

    private async Task EventLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var call = Events.Subscribe(new Empty(), cancellationToken: cancellationToken);
                while (await call.ResponseStream.MoveNext(cancellationToken))
                {
                    var ev = call.ResponseStream.Current;
                    if (ev.BodyCase == ServerEvent.BodyOneofCase.SessionReplaced)
                    {
                        token = null;
                        log.Warn("this account logged in from somewhere else");
                        EventReceived?.Invoke(ev);
                        SetState(ConnectionState.Replaced);
                        return;
                    }
                    EventReceived?.Invoke(ev);
                }
                log.Warn("event stream ended by the server");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex)
            {
                log.Warn($"event stream dropped: {ex.Status.StatusCode} {ex.Status.Detail}");
            }
            catch (Exception ex)
            {
                log.Error($"event stream failed: {ex.Message}");
            }

            if (!await Reconnect(cancellationToken)) return;
        }
    }

    private async Task<bool> Reconnect(CancellationToken cancellationToken)
    {
        token = null;
        SetState(ConnectionState.Reconnecting);
        foreach (var delay in reconnect.Delays())
        {
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { return false; }

            try
            {
                var reply = await LoginOnce(cancellationToken);
                if (reply.SessionToken.Length == 0)
                {
                    FailureReason = "mods no longer match the server";
                    log.Error("reconnect refused: " + FailureReason);
                    SetState(ConnectionState.Failed);
                    return false;
                }
                log.Info("reconnected");
                SetState(ConnectionState.Connected);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (RpcException ex) when (pinRefused || ReconnectPolicy.IsRefusal(ex, steamLogin))
            {
                // Retrying cannot help (and a refused certificate would ask the player again every minute).
                FailureReason = pinRefused ? "the server's new certificate was not trusted" : Economy.EconomyClient.MessageOf(ex);
                log.Error("reconnect refused: " + FailureReason);
                SetState(ConnectionState.Failed);
                return false;
            }
            catch (Exception ex)
            {
                log.Warn($"reconnect attempt failed: {ex.Message}");
            }
        }
        return false; // Delays() has no end: only success, a refusal or the connection's end leave the loop
    }

    private void SetState(ConnectionState next)
    {
        if (state == next) return;
        Interlocked.Exchange(ref reconnectingSinceTicks, next == ConnectionState.Reconnecting ? DateTime.UtcNow.Ticks : 0);
        state = next;
        StateChanged?.Invoke(next);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (eventLoop is { } loop) { try { await loop; } catch (OperationCanceledException) { } }
        channel.Dispose();
        lifetime.Dispose();
    }
}
