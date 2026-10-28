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
    private readonly CancellationTokenSource lifetime = new();
    private volatile string? token;
    private Task? eventLoop;
    private ConnectionState state = ConnectionState.Disconnected;

    public RimlinkConnection(ServerEndpoint endpoint, ICredentialProvider credentials, IPinPrompt pinPrompt, ILog log, ConnectionTransport? transport = null)
    {
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
        channel = GrpcChannel.ForAddress(this.transport.Address, new GrpcChannelOptions
        {
            HttpHandler = this.transport.Handler,
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
    }

    /// <summary>Pins live next to the game's config; the game layer sets the path before the first connection.</summary>
    public static CertificatePinStore Pins { get; set; } = new(Path.Combine(AppContext.BaseDirectory, "rimlink-pins.json"));

    private PinCheck? pendingPin;

    public ServerEndpoint Endpoint => endpoint;
    public ConnectionState State => state;
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
        var registration = credentials.TakeRegistration();
        if (registration is not null && registration.Mods.Count == 0) registration.Mods.AddRange(request.Mods);

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
                if (!await pinPrompt.Confirm(endpoint, pin.Fingerprint, pin.Previous)) throw;
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
        foreach (var delay in ReconnectPolicy.Delays())
        {
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { return false; }

            try
            {
                var reply = await LoginOnce(cancellationToken);
                if (reply.SessionToken.Length == 0)
                {
                    log.Error("reconnect refused: mods no longer match the server");
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
            catch (Exception ex)
            {
                log.Warn($"reconnect attempt failed: {ex.Message}");
            }
        }
        log.Error("could not reconnect within the time limit");
        SetState(ConnectionState.Failed);
        return false;
    }

    private void SetState(ConnectionState next)
    {
        if (state == next) return;
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
