using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.HygieneFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Auth;

/// <summary>Session end, the away baseline fixed at login, known wealth kept and cleared on reset.</summary>
public class SessionBookkeepingTests
{
    private static Session SessionOf(ServerFixture fixture, long playerId) =>
        fixture.Services.GetRequiredService<SessionRegistry>().ByPlayer(playerId)!;

    [Fact]
    public async Task LogoutWritesTheSessionEnd()
    {
        using var fixture = HygieneServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");

        await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LogoutAsync(new Empty(), Bearer(nova.SessionToken));

        await Eventually(async () => await Db(fixture, db => db.Accounts.AnyAsync(a => a.Login == "nova" && a.LastSessionEndAt != null)));
        var account = await Db(fixture, db => db.Accounts.SingleAsync(a => a.Login == "nova"));
        Assert.True(account.LastSessionEndAt >= account.LastSeenAt);
    }

    [Fact]
    public async Task AwaySinceIsTheLastSessionEnd()
    {
        using var fixture = HygieneServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var login = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var end = login.AddHours(3);
        await SetActivity(fixture, nova.Player.PlayerId, login, end);

        await fixture.RegisterAndLogin("nova");

        Assert.Equal(end, SessionOf(fixture, nova.Player.PlayerId).AwaySince);
    }

    [Fact]
    public async Task AwaySinceFallsBackToThePreviousLoginAfterACrash()
    {
        using var fixture = HygieneServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var login = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        // A session end older than the last login belongs to an earlier session: the last one ended in a crash.
        await SetActivity(fixture, nova.Player.PlayerId, login, login.AddDays(-2));

        await fixture.RegisterAndLogin("nova");

        Assert.Equal(login, SessionOf(fixture, nova.Player.PlayerId).AwaySince);
    }

    [Fact]
    public async Task FirstLoginHasNoAwaySince()
    {
        using var fixture = HygieneServer();

        var nova = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel())
            .RegisterAsync(new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "Nova" });

        Assert.Null(SessionOf(fixture, nova.Player.PlayerId).AwaySince);
    }

    [Fact]
    public async Task HeaderWithZeroWealthKeepsTheKnownWealth()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, wealth: 120000, tick: 1000);

        byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova", 2000);
        await Upload(new Protocol.V1.Saves.SavesClient(fixture.CreateChannel()), nova.Token, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova", 2000));

        var player = await Db(fixture, db => db.Players.SingleAsync(p => p.Id == nova.PlayerId));
        Assert.Equal(120000, player.WealthTotal);
        Assert.Equal(60000, player.WealthItems);
    }

    [Fact]
    public async Task ResetClearsColonyAgeAndWealth()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, wealth: 120000);

        var response = await fixture.Rest().PostAsync($"/api/admin/players/{nova.PlayerId}/reset", null);
        response.EnsureSuccessStatusCode();

        var player = await Db(fixture, db => db.Players.SingleAsync(p => p.Id == nova.PlayerId));
        Assert.Null(player.FirstSaveAt);
        Assert.Equal(0, player.WealthTotal);
        Assert.Equal(0, player.WealthItems);
    }
}
