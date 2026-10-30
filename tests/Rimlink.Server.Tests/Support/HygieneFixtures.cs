using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Support;

/// <summary>Settled players with saves and controllable activity, for the archive, sweep, summary and map tests.</summary>
public static class HygieneFixtures
{
    public const long GameDay = 60000;

    public static ServerFixture HygieneServer() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);

    public sealed record Colony(LoginReply Login, long PlayerId, WorldObject Settlement, byte[] LastSave)
    {
        public string Token => Login.SessionToken;
    }

    public static string NameOf(string login) => char.ToUpperInvariant(login[0]) + login[1..];

    /// <summary>Registers, logs in, settles on <paramref name="tile"/> and uploads <paramref name="uploads"/> saves (ticks rising to <paramref name="tick"/>).</summary>
    public static async Task<Colony> Settled(ServerFixture fixture, string login, int tile, long wealth = 50000, long tick = 1000, int uploads = 1)
    {
        string name = NameOf(login);
        var reply = await fixture.RegisterAndLogin(login, playerName: name);
        var settlement = await new Protocol.V1.World.WorldClient(fixture.CreateChannel())
            .SettleAsync(new SettleRequest { Tile = tile, Name = $"{name} Home" }, Bearer(reply.SessionToken));
        byte[] save = [];
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        for (int i = 1; i <= uploads; i++)
        {
            long t = tick - (uploads - i);
            save = MakeSave(ServerFixture.WorldSeed, name, t);
            var header = HeaderFor(save, ServerFixture.WorldSeed, name, t);
            header.WealthTotal = wealth;
            header.WealthItems = wealth / 2;
            await Upload(saves, reply.SessionToken, save, header);
        }
        return new Colony(reply, reply.Player.PlayerId, settlement, save);
    }

    /// <summary>Sets the last login and the last session end of the player's account directly in the database.</summary>
    public static async Task SetActivity(ServerFixture fixture, long playerId, DateTime? lastSeen, DateTime? sessionEnd)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        long accountId = (await db.Players.SingleAsync(p => p.Id == playerId)).AccountId;
        await db.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.LastSeenAt, lastSeen)
            .SetProperty(a => a.LastSessionEndAt, sessionEnd));
    }

    public static async Task<T> Db<T>(ServerFixture fixture, Func<RimlinkDbContext, Task<T>> query)
    {
        using var scope = fixture.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<RimlinkDbContext>());
    }

    /// <summary>Polls until <paramref name="condition"/> holds (fire-and-forget session handlers write asynchronously).</summary>
    public static async Task Eventually(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (waited.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met in time");
            await Task.Delay(25);
        }
    }
}
