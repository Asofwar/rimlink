using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Admin;

public class AdminOperationsTests
{
    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Auth:AdminLogins:0", "admin");

    private static AdminCommand Cmd(string name, params string[] args)
    {
        var c = new AdminCommand { Name = name };
        c.Args.AddRange(args);
        return c;
    }

    [Fact]
    public async Task KickEndsSessionAndLogsAction()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var channel = fixture.CreateChannel();
        var adminClient = new Protocol.V1.Admin.AdminClient(channel);

        var reply = await adminClient.ExecuteAsync(Cmd("kick", "Nova", "be nice"), Bearer(admin.SessionToken));

        Assert.Contains("Nova", reply.Text);
        var ex = await Assert.ThrowsAsync<RpcException>(() => new Protocol.V1.Auth.AuthClient(channel).LogoutAsync(new Empty(), Bearer(nova.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        var actions = await adminClient.ExecuteAsync(Cmd("actions"), Bearer(admin.SessionToken));
        Assert.Contains("kick", actions.Text);
    }

    [Fact]
    public async Task ModeratorCanKickButCannotBan()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var mod = await fixture.RegisterAndLogin("mod", playerName: "Mod");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var adminClient = new Protocol.V1.Admin.AdminClient(fixture.CreateChannel());
        await adminClient.ExecuteAsync(Cmd("role", "Mod", "moderator"), Bearer(admin.SessionToken));
        mod = await fixture.RegisterAndLogin("mod"); // role is read at login

        var banEx = await Assert.ThrowsAsync<RpcException>(() => adminClient.ExecuteAsync(Cmd("ban", "Nova", "1", "x"), Bearer(mod.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, banEx.StatusCode);
        var kicked = await adminClient.ExecuteAsync(Cmd("kick", "Nova"), Bearer(mod.SessionToken));
        Assert.Contains("Nova", kicked.Text);
    }

    [Fact]
    public async Task PlayerCannotRunAdminCommandsExceptRename()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var adminClient = new Protocol.V1.Admin.AdminClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => adminClient.ExecuteAsync(Cmd("online"), Bearer(nova.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);

        await adminClient.ExecuteAsync(Cmd("rename", "Supernova", "Nova Corp"), Bearer(nova.SessionToken));
        var relogin = await fixture.RegisterAndLogin("nova");
        Assert.Equal("Supernova", relogin.Player.Name);
        Assert.Equal("Nova Corp", relogin.Player.FactionName);
    }

    [Fact]
    public async Task BanViaCommandThenPlayerCannotLogin()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var adminClient = new Protocol.V1.Admin.AdminClient(fixture.CreateChannel());

        await adminClient.ExecuteAsync(Cmd("ban", "Nova", "7", "griefing"), Bearer(admin.SessionToken));

        var ex = await Assert.ThrowsAsync<RpcException>(() => new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(PasswordLogin("nova", "pw")).ResponseAsync);
        Assert.Equal(ErrorCode.Banned, ex.ErrorDetail()?.Code);
        var bans = await adminClient.ExecuteAsync(Cmd("bans"), Bearer(admin.SessionToken));
        Assert.Contains("griefing", bans.Text);
    }

    [Fact]
    public async Task ChatSlashCommandRunsWithSenderRole()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());
        using var adminStream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(admin.SessionToken));

        var denied = await Assert.ThrowsAsync<RpcException>(() => chat.SendAsync(new SendRequest { Text = "/kick admin" }, Bearer(nova.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        await chat.SendAsync(new SendRequest { Text = "/kick Nova bye" }, Bearer(admin.SessionToken));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? replyText = null;
        while (replyText is null && await adminStream.ResponseStream.MoveNext(cts.Token))
        {
            var ev = adminStream.ResponseStream.Current;
            if (ev.BodyCase == ServerEvent.BodyOneofCase.Chat && ev.Chat.Kind == MessageKind.System && ev.Chat.ToPlayerId == admin.Player.PlayerId) replyText = ev.Chat.Text;
        }
        Assert.Contains("Nova", replyText);
    }

    [Fact]
    public async Task UnknownCommandReturnsHelp()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");

        var reply = await new Protocol.V1.Admin.AdminClient(fixture.CreateChannel()).ExecuteAsync(Cmd("frobnicate"), Bearer(admin.SessionToken));

        Assert.Contains("kick", reply.Text);
        Assert.Contains("help", reply.Text);
    }

    [Fact]
    public async Task ResetPlayerArchivesSavesAndRemovesObjects()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var world = new Protocol.V1.World.WorldClient(fixture.CreateChannel());
        await world.SettleAsync(new SettleRequest { Tile = 12, Name = "Home" }, Bearer(nova.SessionToken));
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova");
        await Upload(saves, nova.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova"));

        await new Protocol.V1.Admin.AdminClient(fixture.CreateChannel()).ExecuteAsync(Cmd("reset", "Nova"), Bearer(admin.SessionToken));

        var relogin = await fixture.RegisterAndLogin("nova");
        Assert.False(relogin.HasSave);
        Assert.False(relogin.HasSettlement);
        Assert.Empty((await world.ListObjectsAsync(new Empty(), Bearer(relogin.SessionToken))).Objects);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(fixture.DataDir, "archive"), "*.zst", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ShutdownBroadcastsAndStopsHost()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova");
        using var stream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(nova.SessionToken));
        var stopping = new TaskCompletionSource();
        fixture.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());

        await new Protocol.V1.Admin.AdminClient(fixture.CreateChannel()).ExecuteAsync(Cmd("shutdown", "0", "maintenance"), Bearer(admin.SessionToken));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ServerEvent? shutdown = null;
        try
        {
            while (shutdown is null && await stream.ResponseStream.MoveNext(cts.Token))
                if (stream.ResponseStream.Current.BodyCase == ServerEvent.BodyOneofCase.Shutdown) shutdown = stream.ResponseStream.Current;
        }
        catch (RpcException) { }
        Assert.Equal("maintenance", shutdown?.Shutdown.Reason);
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RecreateWorldArchivesEverythingAndKicksPlayers()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var world = new Protocol.V1.World.WorldClient(fixture.CreateChannel());
        await world.SettleAsync(new SettleRequest { Tile = 3, Name = "Home" }, Bearer(nova.SessionToken));

        await new Protocol.V1.Admin.AdminClient(fixture.CreateChannel()).ExecuteAsync(Cmd("recreateworld"), Bearer(admin.SessionToken));

        var ex = await Assert.ThrowsAsync<RpcException>(() => new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(PasswordLogin("nova", "pw")).ResponseAsync);
        Assert.Equal(ErrorCode.WorldNotCreated, ex.ErrorDetail()?.Code);
        var adminAgain = await fixture.RegisterAndLogin("admin");
        Assert.False(adminAgain.WorldExists);
        Assert.Empty((await world.ListObjectsAsync(new Empty(), Bearer(adminAgain.SessionToken))).Objects);
    }
}
