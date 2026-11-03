using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Rimlink.Client.Net;
using Rimlink.Client.State;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using DbLive = Rimlink.Server.Storage.Entities.LiveSession;
using DbRaid = Rimlink.Server.Storage.Entities.Raid;

namespace Rimlink.Client.Tests.Net;

public sealed class LoginRecoveryTests
{
    [Fact]
    public async Task FrozenWalletDoesNotHideMailboxRaidsOrPendingLiveEndsAndRetryRecoversIt()
    {
        using var fixture = EconomyFixtures.EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova"); var bob = await fixture.RegisterAndLogin("bob");
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            db.Raids.Add(new DbRaid { AttackerPlayerId = bob.Player.PlayerId, TargetPlayerId = nova.Player.PlayerId, State = RaidStateDb.Queued,
                CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1), Pawns = new PawnList().ToByteArray() });
            db.LiveSessions.Add(new DbLive { HostPlayerId = nova.Player.PlayerId, GuestPlayerId = bob.Player.PlayerId, Kind = (int)LiveKind.LiveVisit,
                State = LiveStateDb.Ending, CreatedAt = DateTime.UtcNow, Pawns = new PawnList().ToByteArray() });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<MailboxService>().Create(nova.Player.PlayerId, null, ParcelKindDb.Admin,
                EconomyFixtures.Items(("Steel", 1)), null, 0, null, ignoreMailboxLimit: true);
        }
        await EconomyFixtures.Fund(fixture, nova.Player.PlayerId, 50);
        var freeze = fixture.Services.GetRequiredService<FreezeState>(); freeze.Frozen = true;
        await using var connection = ClientFixtures.Connect(fixture, "nova"); await connection.ConnectAsync(CancellationToken.None);
        var state = new SharedState();
        var recovery = new RecoveryCoordinator(connection, state, null, new ConsoleLog(), action => { action(); return Task.CompletedTask; });

        Assert.False(await recovery.LoadStatesAsync(CancellationToken.None));

        Assert.Single(state.Economy.Pending); Assert.Single(state.Raids.Incoming); Assert.Single(state.Live.PendingEnds);
        state.Live.PendingEnds.Clear(); // the loaded game settled it; another wallet retry must not resurrect it
        freeze.Frozen = false;
        Assert.True(await recovery.LoadStatesAsync(CancellationToken.None));
        Assert.Equal(50, state.Economy.Balance); Assert.Empty(state.Live.PendingEnds);
    }

    [Fact]
    public async Task ResultsOfAnOldSessionAreNeverAppliedToTheNewState()
    {
        using var fixture = EconomyFixtures.EconomyServer();
        await using var connection = await ClientFixtures.Connected(fixture, "nova");
        await EconomyFixtures.Fund(fixture, connection.Login!.Player.PlayerId, 50);
        var state = new SharedState();
        var recovery = new RecoveryCoordinator(connection, state, null, new ConsoleLog(), action => { action(); return Task.CompletedTask; }, () => false);
        Assert.False(await recovery.LoadStatesAsync(CancellationToken.None));
        Assert.Equal(0, state.Economy.Balance);
    }
}
