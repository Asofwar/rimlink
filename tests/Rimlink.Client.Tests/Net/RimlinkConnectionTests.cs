using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class RimlinkConnectionTests
{
    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Auth:AdminLogins:0", "admin");

    private sealed class PasswordCredentials(string login, string password) : ICredentialProvider
    {
        public int Calls;

        public Task<LoginRequest> BuildLogin(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new LoginRequest { Password = new PasswordCredential { Login = login, Password = password } });
        }
    }

    private sealed class NoPrompt : IPinPrompt
    {
        public Task<bool> Confirm(ServerEndpoint endpoint, string fingerprint, string? previous) => Task.FromResult(true);
    }

    private static RimlinkConnection Connect(ServerFixture fixture, ICredentialProvider credentials, ReconnectPolicy? reconnect = null) =>
        new(new ServerEndpoint("test", 1), credentials, new NoPrompt(), new ConsoleLog(),
            new ConnectionTransport(fixture.Server.BaseAddress, fixture.Server.CreateHandler()), reconnect);

    /// <summary>The real back-off scaled down: the 2-minute mark becomes half a second.</summary>
    private static ReconnectPolicy Fast() => new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500));

    private static async Task<ServerEvent> WaitFor(RimlinkConnection connection, ServerEvent.BodyOneofCase body, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<ServerEvent>();
        void Handler(ServerEvent ev) { if (ev.BodyCase == body) tcs.TrySetResult(ev); }
        connection.EventReceived += Handler;
        try { return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10)); }
        finally { connection.EventReceived -= Handler; }
    }

    private static async Task<ConnectionState> WaitForAny(RimlinkConnection connection, TimeSpan timeout, params ConnectionState[] states)
    {
        var tcs = new TaskCompletionSource<ConnectionState>();
        void Handler(ConnectionState s) { if (states.Contains(s)) tcs.TrySetResult(s); }
        connection.StateChanged += Handler;
        try
        {
            if (states.Contains(connection.State)) return connection.State;
            return await tcs.Task.WaitAsync(timeout);
        }
        catch (TimeoutException) { return connection.State; }
        finally { connection.StateChanged -= Handler; }
    }

    private static async Task WaitForState(RimlinkConnection connection, ConnectionState state, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource();
        void Handler(ConnectionState s) { if (s == state) tcs.TrySetResult(); }
        connection.StateChanged += Handler;
        try
        {
            if (connection.State == state) return;
            await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
        }
        finally { connection.StateChanged -= Handler; }
    }

    [Fact]
    public async Task ConnectLoginsAndReceivesNotice()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await using var connection = Connect(fixture, new PasswordCredentials("nova", "pw"));

        var reply = await connection.ConnectAsync(CancellationToken.None);
        var notice = WaitFor(connection, ServerEvent.BodyOneofCase.Notice);
        fixture.Services.GetRequiredService<EventBus>().Broadcast(new ServerEvent { Notice = new Notice { Text = "hi" } });

        Assert.Equal("Nova", reply.Player.Name);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal("hi", (await notice).Notice.Text);
        Assert.Equal(1, (await connection.World.GetAsync(new Empty())).NpcTiles.Count + 1); // authenticated call works
    }

    private sealed class RegisteringCredentials(string login, string password, string playerName) : ICredentialProvider
    {
        private RegisterRequest? pending = new() { Login = login, Password = password, PlayerName = playerName };

        public Task<LoginRequest> BuildLogin(CancellationToken cancellationToken) =>
            Task.FromResult(new LoginRequest { Password = new PasswordCredential { Login = login, Password = password } });

        public RegisterRequest? TakeRegistration()
        {
            var request = pending;
            pending = null;
            return request;
        }
    }

    /// <summary>A new account whose login request carries mods and mod settings hashes, as the game builds it.</summary>
    private sealed class RegisteringWithMods : ICredentialProvider
    {
        private RegisterRequest? pending = new() { Login = "nova", Password = "pw", PlayerName = "Nova" };

        public Task<LoginRequest> BuildLogin(CancellationToken cancellationToken)
        {
            var request = new LoginRequest { Password = new PasswordCredential { Login = "nova", Password = "pw" } };
            request.Mods.Add(new ModRef { PackageId = Rimlink.Protocol.ModContent.RimlinkPackageId, Position = 0, FilesHash = "h" });
            request.Mods.Add(new ModRef { PackageId = "a.one", Position = 1, FilesHash = "h" });
            request.ModSettings.Add(new ModSettingsHash { PackageId = "a.one", Handle = "OneMod", Hash = Rimlink.Protocol.ModContent.NoSettings });
            return Task.FromResult(request);
        }

        public RegisterRequest? TakeRegistration()
        {
            var request = pending;
            pending = null;
            return request;
        }
    }

    [Fact]
    public async Task RegistrationCarriesTheModSettings()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());
        var pack = new ModpackList();
        pack.Entries.Add(new ModpackEntry { Position = 0, PackageId = Rimlink.Protocol.ModContent.RimlinkPackageId });
        pack.Entries.Add(new ModpackEntry { Position = 1, PackageId = "a.one" });
        await modpack.SetAsync(pack, GrpcTestExtensions.Bearer(admin.SessionToken));
        var configs = new ModConfigList();
        configs.Configs.Add(new ModConfig { PackageId = "a.one", Handle = "OneMod", Content = Google.Protobuf.ByteString.CopyFromUtf8("<SettingsBlock/>") });
        await modpack.SetConfigsAsync(configs, GrpcTestExtensions.Bearer(admin.SessionToken));
        await using var connection = Connect(fixture, new RegisteringWithMods());

        var reply = await connection.ConnectAsync(CancellationToken.None);

        Assert.Empty(reply.SessionToken);
        Assert.Single(reply.ModpackDiff.Configs);
    }

    [Fact]
    public async Task ConnectRegistersWhenCredentialsAskForIt()
    {
        using var fixture = Server();
        await using var connection = Connect(fixture, new RegisteringCredentials("nova", "pw", "Nova"));

        var reply = await connection.ConnectAsync(CancellationToken.None);

        Assert.Equal("Nova", reply.Player.Name);
        Assert.Equal(ConnectionState.Connected, connection.State);
        // The account exists now: a plain password login works from another connection.
        await using var second = Connect(fixture, new PasswordCredentials("nova", "pw"));
        Assert.Equal("Nova", (await second.ConnectAsync(CancellationToken.None)).Player.Name);
    }

    [Fact]
    public async Task ModpackMismatchReturnsDiffAndStaysDisconnected()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var list = new ModpackList();
        list.Entries.Add(new ModpackEntry { PackageId = "ludeon.rimworld", Version = "1.7" });
        await new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel()).SetAsync(list, GrpcTestExtensions.Bearer(admin.SessionToken));
        await fixture.RegisterAndLogin("nova");
        await using var connection = Connect(fixture, new PasswordCredentials("nova", "pw"));

        var reply = await connection.ConnectAsync(CancellationToken.None);

        Assert.Empty(reply.SessionToken);
        Assert.Single(reply.ModpackDiff.Missing);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task SessionReplacedStopsReconnect()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova");
        var credentials = new PasswordCredentials("nova", "pw");
        await using var connection = Connect(fixture, credentials);
        await connection.ConnectAsync(CancellationToken.None);
        int callsAfterConnect = credentials.Calls;

        await fixture.RegisterAndLogin("nova"); // same account from "elsewhere"
        await WaitForState(connection, ConnectionState.Replaced);
        await Task.Delay(1500); // the first reconnect delay is 1 s; nothing must happen

        Assert.Equal(ConnectionState.Replaced, connection.State);
        Assert.Equal(callsAfterConnect, credentials.Calls);
    }

    [Fact]
    public async Task DroppedStreamReconnectsAndResubscribes()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var credentials = new PasswordCredentials("nova", "pw");
        await using var connection = Connect(fixture, credentials);
        await connection.ConnectAsync(CancellationToken.None);
        var states = new List<ConnectionState>();
        connection.StateChanged += states.Add;

        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().Kick(Actor.Api, nova.Player.PlayerId, "test");
        }
        await WaitForState(connection, ConnectionState.Reconnecting);
        await WaitForState(connection, ConnectionState.Connected, TimeSpan.FromSeconds(15));
        var notice = WaitFor(connection, ServerEvent.BodyOneofCase.Notice);
        fixture.Services.GetRequiredService<EventBus>().Broadcast(new ServerEvent { Notice = new Notice { Text = "back" } });

        Assert.Equal("back", (await notice).Notice.Text);
        Assert.Contains(ConnectionState.Reconnecting, states);
        Assert.Equal(2, credentials.Calls);
    }

    [Fact]
    public async Task DisconnectLogsOutAndEndsTheSession()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova");
        var connection = Connect(fixture, new PasswordCredentials("nova", "pw"));
        await connection.ConnectAsync(CancellationToken.None);

        await connection.DisconnectAsync();

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        using var scope = fixture.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AdminOperations>().ListOnline(Actor.Api));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task DroppedNetworkBreaksTheStreamAndReconnectsWhenItIsBack()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var credentials = new PasswordCredentials("nova", "pw");
        await using var connection = Connect(fixture, credentials);
        await connection.ConnectAsync(CancellationToken.None);

        // Dev-only outage: the open event stream breaks as on a real network loss, then the usual reconnect runs.
        connection.DropNetwork(TimeSpan.FromSeconds(2));
        await WaitForState(connection, ConnectionState.Reconnecting);
        await WaitForState(connection, ConnectionState.Connected, TimeSpan.FromSeconds(20));
        var notice = WaitFor(connection, ServerEvent.BodyOneofCase.Notice);
        fixture.Services.GetRequiredService<EventBus>().Broadcast(new ServerEvent { Notice = new Notice { Text = "back" } });

        Assert.Equal("back", (await notice).Notice.Text);
        Assert.True(credentials.Calls >= 2);
    }

    [Fact]
    public async Task OutageLongerThanTheOfferKeepsReconnectingUntilTheNetworkIsBack()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await using var connection = Connect(fixture, new PasswordCredentials("nova", "pw"), Fast());
        await connection.ConnectAsync(CancellationToken.None);
        var states = new List<ConnectionState>();
        connection.StateChanged += states.Add;

        // Six times the scaled 2-minute mark: the old budget gave up long before the network came back.
        connection.DropNetwork(TimeSpan.FromSeconds(3));
        await WaitForState(connection, ConnectionState.Reconnecting);
        var settled = await WaitForAny(connection, TimeSpan.FromSeconds(20), ConnectionState.Connected, ConnectionState.Failed);

        Assert.Equal(ConnectionState.Connected, settled);
        Assert.DoesNotContain(ConnectionState.Failed, states);
    }

    [Fact]
    public async Task LongOutageIsFlaggedWhileStillReconnectingAndClearedOnReturn()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await using var connection = Connect(fixture, new PasswordCredentials("nova", "pw"), Fast());
        await connection.ConnectAsync(CancellationToken.None);
        Assert.Null(connection.ReconnectingSince);

        connection.DropNetwork(TimeSpan.FromSeconds(2));
        await WaitForState(connection, ConnectionState.Reconnecting);
        Assert.NotNull(connection.ReconnectingSince);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!connection.OutageIsLong && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.True(connection.OutageIsLong);
        Assert.Equal(ConnectionState.Reconnecting, connection.State);
        await WaitForState(connection, ConnectionState.Connected, TimeSpan.FromSeconds(20));
        Assert.False(connection.OutageIsLong);
        Assert.Null(connection.ReconnectingSince);
    }

    [Fact]
    public async Task RefusedReconnectFailsAtOnceWithTheServersReason()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await using var connection = Connect(fixture, new PasswordCredentials("nova", "pw"), Fast());
        await connection.ConnectAsync(CancellationToken.None);

        using (var scope = fixture.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AdminOperations>();
            await admin.Ban(Actor.Api, nova.Player.PlayerId, null, null, "griefing", null);
            await admin.Kick(Actor.Api, nova.Player.PlayerId, "banned");
        }
        // The kick reaches the client through its event stream: until then it is still Connected. Wait for the drop first.
        await WaitForAny(connection, TimeSpan.FromSeconds(5), ConnectionState.Reconnecting, ConnectionState.Failed);
        var settled = await WaitForAny(connection, TimeSpan.FromSeconds(5), ConnectionState.Failed, ConnectionState.Connected);

        Assert.Equal(ConnectionState.Failed, settled);
        Assert.Contains("griefing", connection.FailureReason);
        Assert.Null(connection.ReconnectingSince);
    }

    /// <summary>A Steam login whose ticket the server cannot verify while <see cref="SteamDown"/> (as when Steam's Web API is unreachable).</summary>
    private sealed class SteamCredentials : ICredentialProvider
    {
        public volatile bool SteamDown;

        public Task<LoginRequest> BuildLogin(CancellationToken cancellationToken) => Task.FromResult(new LoginRequest
        {
            SteamTicket = ByteString.CopyFrom(SteamDown ? "unverifiable"u8.ToArray() : FakeSteamVerifier.Ticket(76561190000000042)),
        });
    }

    [Fact]
    public async Task SteamThatCannotVerifyTheTicketDuringAReconnectIsWaitedFor()
    {
        using var fixture = Server();
        var steam = new SteamCredentials();
        await using var connection = Connect(fixture, steam, Fast());
        var login = await connection.ConnectAsync(CancellationToken.None);

        steam.SteamDown = true;
        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().Kick(Actor.Api, login.Player.PlayerId, "test");
        }
        await WaitForState(connection, ConnectionState.Reconnecting);
        var whileDown = await WaitForAny(connection, TimeSpan.FromSeconds(1.5), ConnectionState.Failed, ConnectionState.Connected);
        steam.SteamDown = false;
        var afterwards = await WaitForAny(connection, TimeSpan.FromSeconds(10), ConnectionState.Failed, ConnectionState.Connected);

        Assert.Equal(ConnectionState.Reconnecting, whileDown);
        Assert.Equal(ConnectionState.Connected, afterwards);
    }

    [Fact]
    public async Task CallsFailWhileTheNetworkIsDown()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova");
        await using var connection = Connect(fixture, new PasswordCredentials("nova", "pw"));
        await connection.ConnectAsync(CancellationToken.None);

        connection.DropNetwork(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => connection.World.GetAsync(new Empty()).ResponseAsync);
        Assert.Equal(Grpc.Core.StatusCode.Unavailable, ex.StatusCode);
    }

    [Fact]
    public async Task AwaySummaryRoundTrips()
    {
        using var fixture = Server();
        await using var connection = await Support.ClientFixtures.Connected(fixture, "nova");

        var summary = await connection.AwayApi.SummaryAsync(new Empty());

        Assert.Empty(summary.Parcels);
        Assert.NotNull(summary.Since); // RegisterAndLogin logged in once before this connection: the baseline is that login
    }
}
