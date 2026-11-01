using Rimlink.Client.Economy;
using Rimlink.Client.Social;
using Rimlink.Client.Net;
using Rimlink.Client.State;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;

namespace Rimlink.Client.Tests.Social;

public sealed class CommunityClientTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-community-outbox-" + Guid.NewGuid().ToString("N"));
    private TransferOutbox Outbox() => new(Path.Combine(dir, "outbox.json"), new ConsoleLog());
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    [Fact]
    public void InvalidationImmediatelyClearsPrivateDataAndRejectsOldFetch()
    {
        var state = new SharedState();
        var snapshot = new CommunityState { Alliance = new AllianceInfo { Id = 5 } };
        snapshot.Alliance.Members.Add(new AllianceMemberInfo { Player = new PlayerRef { PlayerId = 12 } });
        snapshot.Chat.Add(new AllianceChatLine { Id = 1, Text = "private" });
        Assert.True(state.Community.Load(snapshot, state.Community.Revision));
        Assert.True(state.Community.IsAlly(12));
        long old = state.Community.Revision;
        state.Apply(new ServerEvent { CommunityChanged = new CommunityChanged() });
        Assert.Null(state.Community.Current.Alliance);
        Assert.Empty(state.Community.Current.Chat);
        Assert.False(state.Community.Load(snapshot, old));
        Assert.False(state.Community.IsAlly(12));
    }

    [Fact]
    public async Task LostAnswerReplaysExactIntentAfterOutboxReload()
    {
        using var f = EconomyServer();
        await using var connection = await ClientFixtures.Connected(f, "alice");
        var client = new CommunityClient(connection);
        var request = new CommunityRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Action = CommunityAction.AllianceCreate, Text = "Pioneers" };
        var applied = await client.Mutate(request, CancellationToken.None); // the server applied; pretend the answer was lost
        Outbox().Add(request);
        var reloaded = Outbox();
        var replay = await reloaded.Replay(new OutboxClients(new EconomyClient(connection), null, null, Community: client), CancellationToken.None);
        Assert.Equal(1, replay.Completed);
        Assert.Empty(reloaded.Entries);
        Assert.Equal(applied.AllianceId, (await client.Get(CancellationToken.None)).Alliance.Id);
    }

    [Fact]
    public async Task DefinitiveRefusalDropsSilverOnlyOperationWithoutPhysicalRefund()
    {
        using var f = EconomyServer();
        await using var connection = await ClientFixtures.Connected(f, "alice");
        var outbox = Outbox();
        outbox.Add(new CommunityRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Action = CommunityAction.TreasuryWithdraw, AllianceId = 99, Silver = 100 });
        var report = await outbox.Replay(new OutboxClients(new EconomyClient(connection), null, null, Community: new CommunityClient(connection)), CancellationToken.None);
        Assert.Equal(1, report.Refused);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task MissingCommunityReplayClientLeavesRequestPending()
    {
        using var f = EconomyServer();
        await using var connection = await ClientFixtures.Connected(f, "alice");
        var outbox = Outbox();
        var request = new CommunityRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Action = CommunityAction.FriendInvite, PlayerId = 99 };
        var entry = outbox.Add(request);
        Assert.Equal(request, TransferOutbox.Decode(entry));
        var result = await outbox.Replay(new OutboxClients(new EconomyClient(connection), null, null), CancellationToken.None);
        Assert.Equal(1, result.Pending);
        Assert.Single(outbox.Entries);
    }
}
