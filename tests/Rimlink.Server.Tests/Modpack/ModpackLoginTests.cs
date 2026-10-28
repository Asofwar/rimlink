using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Modpack;

public class ModpackLoginTests
{
    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Auth:AdminLogins:0", "admin");

    private static ModpackList List(params (string id, string version)[] mods)
    {
        var list = new ModpackList();
        int position = 0;
        foreach (var (id, version) in mods)
            list.Entries.Add(new ModpackEntry { Position = position++, PackageId = id, Version = version, Kind = EntryKind.Required, WorkshopId = 2009463077 });
        return list;
    }

    private static LoginRequest LoginWithMods(string login, params (string id, string version)[] mods)
    {
        var request = PasswordLogin(login, "pw");
        foreach (var (id, version) in mods) request.Mods.Add(new ModRef { PackageId = id, Version = version });
        return request;
    }

    [Fact]
    public async Task MismatchReturnsDiffWithoutToken()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());
        await modpack.SetAsync(List(("ludeon.rimworld", "1.7"), ("brrainz.harmony", "2.4.3")), Bearer(admin.SessionToken));
        await fixture.RegisterAndLogin("nova");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var reply = await auth.LoginAsync(LoginWithMods("nova", ("ludeon.rimworld", "1.7")));

        Assert.Empty(reply.SessionToken);
        Assert.NotNull(reply.ModpackDiff);
        Assert.Equal(["brrainz.harmony"], reply.ModpackDiff.Missing.Select(m => m.PackageId));
        Assert.Equal(2009463077UL, reply.ModpackDiff.Missing[0].WorkshopId);
        Assert.Equal(2, reply.ModpackDiff.ServerList.Count);
    }

    [Fact]
    public async Task MatchingModsLogIn()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        await new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel()).SetAsync(List(("ludeon.rimworld", "1.7")), Bearer(admin.SessionToken));
        await fixture.RegisterAndLogin("nova");

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(LoginWithMods("nova", ("Ludeon.RimWorld_steam", "1.7")));

        Assert.NotEmpty(reply.SessionToken);
        Assert.Null(reply.ModpackDiff);
    }

    [Fact]
    public async Task RegisterIsCheckedAgainstTheModpackToo()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        await new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel()).SetAsync(List(("ludeon.rimworld", "1.7")), Bearer(admin.SessionToken));

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).RegisterAsync(new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "Nova" });

        Assert.Empty(reply.SessionToken);
        Assert.Single(reply.ModpackDiff.Missing);
    }

    [Fact]
    public async Task AdminBypassesModpackCheck()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        await new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel()).SetAsync(List(("ludeon.rimworld", "1.7")), Bearer(admin.SessionToken));

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(LoginWithMods("admin"));

        Assert.NotEmpty(reply.SessionToken);
    }

    [Fact]
    public async Task NonAdminCannotSetModpackButCanGetIt()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());
        await modpack.SetAsync(List(("ludeon.rimworld", "1.7")), Bearer(admin.SessionToken));
        var register = new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "Nova" };
        register.Mods.Add(new ModRef { PackageId = "ludeon.rimworld", Version = "1.7" });
        var nova = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).RegisterAsync(register);

        var ex = await Assert.ThrowsAsync<RpcException>(() => modpack.SetAsync(List(), Bearer(nova.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);

        var list = await modpack.GetAsync(new Empty(), Bearer(nova.SessionToken));
        Assert.Equal(["ludeon.rimworld"], list.Entries.Select(e => e.PackageId));
    }
}
