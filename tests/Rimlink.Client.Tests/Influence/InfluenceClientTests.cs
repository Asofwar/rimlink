using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Influence;
using Rimlink.Client.Net;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Influence;

public sealed class InfluenceClientTests
{
    [Fact]
    public async Task FullCapacityRestoresTheColonyWithALocalizableNoticeAndNoPublication()
    {
        using var fixture = await InfluenceFixtures.Create();
        Assert.Null((await fixture.Send(InfluenceFixtures.Publication(await fixture.ConfirmSurface(peer: true), true), peer: true)).Error);
        fixture.Server.Services.GetRequiredService<SessionRegistry>().End(fixture.Peer.SessionToken);
        long archive;
        using (var scope = fixture.Server.Services.CreateScope())
            archive = Assert.IsType<ArchiveOutcome.Archived>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(fixture.Peer.Player.PlayerId, "admin", Actor.Api)).ArchiveId;
        await fixture.SeedPublished(128);
        using (var scope = fixture.Server.Services.CreateScope())
        {
            var restored = Assert.IsType<RestoreOutcome.Restored>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archive, "api"));
            var reply = new ColonyRestore { Restored = true, InfluenceNoticeKey = restored.InfluenceNoticeKey };
            Assert.Equal("Rimlink.Influence.CapacityRestore", InfluenceClient.RestoreNoticeKey(reply));
            Assert.NotEqual(0, restored.Settlement.Id);
        }
        Assert.Equal(128, (await fixture.Snapshot()).Sources.Count);
        await fixture.Db(db =>
        {
            Assert.False(db.WorldObjects.Single(o => o.OwnerPlayerId == fixture.Peer.Player.PlayerId).InfluenceEnabled);
            return Task.CompletedTask;
        });
        Assert.Null(InfluenceClient.RestoreNoticeKey(new ColonyRestore { Restored = false, InfluenceNoticeKey = "Influence.CapacityRestore" }));
        Assert.Null(InfluenceClient.RestoreNoticeKey(new ColonyRestore { Restored = true, InfluenceNoticeKey = "unknown" }));
    }

    [Fact]
    public async Task LostSuccessReplaysExactIntentWithoutChangingPublicationAgain()
    {
        using var fixture = await InfluenceFixtures.Create();
        var setting = await fixture.ConfirmSurface();
        await using var connection = ClientFixtures.Connect(fixture.Server, "owner");
        await connection.ConnectAsync(CancellationToken.None);
        var client = new InfluenceClient(connection);
        var request = InfluenceFixtures.Publication(setting, true);
        var applied = await client.Mutate(request, CancellationToken.None);
        string file = Path.Combine(fixture.Server.DataDir, "influence-outbox.json");
        new TransferOutbox(file, new ConsoleLog()).Add(request);
        var reloaded = new TransferOutbox(file, new ConsoleLog());
        var result = await reloaded.Replay(new OutboxClients(new EconomyClient(connection), null, null, Influence: client), CancellationToken.None);
        Assert.Equal(1, result.Completed);
        Assert.Empty(reloaded.Entries);
        var current = await client.GetSetting(new InfluenceTarget { SettlementId = fixture.HomeId }, CancellationToken.None);
        Assert.True(current.Enabled);
        Assert.Equal(applied.AppliedVersion, current.Version);
    }

    [Fact]
    public async Task AStaleEnableIsRefusedOnReplayWithoutARefundOrPublication()
    {
        using var fixture = await InfluenceFixtures.Create();
        var setting = await fixture.ConfirmSurface();
        await using var connection = ClientFixtures.Connect(fixture.Server, "owner");
        await connection.ConnectAsync(CancellationToken.None);
        var client = new InfluenceClient(connection);
        var outbox = new TransferOutbox(Path.Combine(fixture.Server.DataDir, "outbox.json"), new ConsoleLog());
        outbox.Add(InfluenceFixtures.Publication(setting, true));
        await client.Mutate(InfluenceFixtures.Publication(setting, false), CancellationToken.None);
        var result = await outbox.Replay(new OutboxClients(new EconomyClient(connection), null, null, Influence: client), CancellationToken.None);
        Assert.Equal(1, result.Refused);
        Assert.Empty(outbox.Entries);
        Assert.False((await client.GetSetting(new InfluenceTarget { SettlementId = fixture.HomeId }, CancellationToken.None)).Enabled);
    }

    [Fact]
    public async Task MissingReplayClientKeepsIntentAndUnavailableIsNotDefinitive()
    {
        using var fixture = await InfluenceFixtures.Create();
        var setting = await fixture.ConfirmSurface();
        await using var connection = ClientFixtures.Connect(fixture.Server, "owner");
        await connection.ConnectAsync(CancellationToken.None);
        var outbox = new TransferOutbox(Path.Combine(fixture.Server.DataDir, "outbox.json"), new ConsoleLog());
        var intent = InfluenceFixtures.Publication(setting, false);
        var entry = outbox.Add(intent);
        Assert.Equal(intent, TransferOutbox.Decode(entry));
        var result = await outbox.Replay(new OutboxClients(new EconomyClient(connection), null, null), CancellationToken.None);
        Assert.Equal(1, result.Pending);
        Assert.Single(outbox.Entries);
        var unavailable = new RpcException(new Status(StatusCode.Unavailable, "snapshot unavailable"), new Metadata { { RpcErrorDetail.Trailer, new ErrorDetail { Code = ErrorCode.InfluenceUnavailable }.ToByteArray() } });
        Assert.False(TransferOutbox.IsDefinitive(unavailable));
    }
}
