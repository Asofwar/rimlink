using Grpc.Core;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Tests.Support;

public sealed record LiveClients(Protocol.V1.Live.LiveClient Live, Protocol.V1.Raids.RaidsClient Raids, Protocol.V1.Mailbox.MailboxClient Mailbox,
    Protocol.V1.Auth.AuthClient AuthApi, Protocol.V1.Events.EventsClient Events, Metadata Auth, LoginReply Login)
{
    public long PlayerId => Login.Player.PlayerId;

    public AsyncServerStreamingCall<ServerEvent> Subscribe() => Events.Subscribe(new Empty(), Auth);
}

public static class LiveFixtures
{
    /// <summary>
    /// Raid server with a one-second timeout sweep. Invitations keep the configured minute, so a slow machine never sees one lapse in the
    /// middle of a test; the tests of the lapse shorten it with <see cref="ShortInvites"/>.
    /// </summary>
    public static ServerFixture LiveServer() => RaidFixtures.RaidServer().WithSetting("Live:SweepSeconds", "1");

    /// <summary>Invitations lapse after two seconds.</summary>
    public static ServerFixture ShortInvites(this ServerFixture fixture) => fixture.WithSetting("Live:InviteSeconds", "2");

    public static LiveClients LiveClientsOf(ServerFixture fixture, LoginReply login)
    {
        var channel = fixture.CreateChannel();
        return new LiveClients(new Protocol.V1.Live.LiveClient(channel), new Protocol.V1.Raids.RaidsClient(channel), new Protocol.V1.Mailbox.MailboxClient(channel),
            new Protocol.V1.Auth.AuthClient(channel), new Protocol.V1.Events.EventsClient(channel), GrpcTestExtensions.Bearer(login.SessionToken), login);
    }

    /// <summary>Registers, logs in and reports the player at home (unless <paramref name="atHome"/> is false).</summary>
    public static async Task<LiveClients> Connected(ServerFixture fixture, string login, bool atHome = true)
    {
        var reply = await fixture.RegisterAndLogin(login, playerName: char.ToUpperInvariant(login[0]) + login[1..]);
        var clients = LiveClientsOf(fixture, reply);
        if (atHome) await clients.AuthApi.SetAtHomeAsync(new AtHomeRequest { AtHome = true }, clients.Auth);
        return clients;
    }

    public static LiveProposal Propose(long hostId, LiveKind kind, int pawns = 2, string? requestId = null, long tick = 100)
    {
        var p = new LiveProposal
        {
            ClientRequestId = requestId ?? Guid.NewGuid().ToString("N"),
            HostPlayerId = hostId,
            Kind = kind,
            TimingFallback = RaidTiming.TimingInstant,
            Arrival = RaidArrival.ArrivalEdge,
            GameTick = tick,
            Pawns = new PawnList(),
        };
        for (int i = 0; i < pawns; i++) p.Pawns.Pawns.Add(EconomyFixtures.Pawn("Colonist", "Guest" + i));
        return p;
    }

    /// <summary>A host's report for a live session: every pawn alive with a blob, no loot.</summary>
    public static RaidReport AllAlive(long raidId, long liveSessionId, int pawns, long tick = 500)
    {
        var report = new RaidReport { RaidId = raidId, LiveSessionId = liveSessionId, GameTick = tick, Loot = new ItemList(), Captives = new PawnList() };
        for (int i = 0; i < pawns; i++) report.Outcomes.Add(new PawnOutcomeMsg { Index = i, Outcome = PawnOutcome.OutcomeAlive, Blob = EconomyFixtures.Pawn("Colonist", "Guest" + i) });
        return report;
    }
}
