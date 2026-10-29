using RimBridgeServer.Sdk;
using Rimlink.Client;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Live;
using Rimlink.Client.Game.Visits;
using Verse;

namespace Rimlink.BridgeTools;

public sealed class StatusTools
{
    [Tool("rimlink/status",
        Description = "Rimlink session state: program state, top window, connection, player, world, players and settlements, economy, raids, live session, visit, outbox",
        ResultDescription = "success plus a snapshot; session is null when there is no Rimlink session")]
    public Task<object> Status(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx, () => new
    {
        success = true,
        programState = Current.ProgramState.ToString(),
        topWindow = Bridge.TopWindowType(),
        // A second Rimlink.Client loaded from the BridgeTools folder would show up here with an empty session.
        clientAssembly = typeof(RimlinkSession).Assembly.Location,
        clientVersion = ClientInfo.Version,
        session = RimlinkSession.Active ? Session() : null,
    });

    private static object Session()
    {
        var state = RimlinkSession.State;
        long me = RimlinkSession.MyPlayerId;
        var live = state.Live.Current;
        return new
        {
            endpoint = $"{RimlinkSession.Endpoint!.Display} ({RimlinkSession.Endpoint.Host}:{RimlinkSession.Endpoint.Port})",
            connection = RimlinkSession.LastState.ToString(),
            reconnectingForSec = RimlinkSession.Connection?.ReconnectingSince is { } since ? (int?)(DateTime.UtcNow - since).TotalSeconds : null,
            outageIsLong = RimlinkSession.Connection?.OutageIsLong ?? false,
            failureReason = RimlinkSession.Connection?.FailureReason,
            player = new { id = me, name = RimlinkSession.Login!.Player.Name, role = RimlinkSession.Login.Role.ToString() },
            world = new { exists = RimlinkSession.World?.Exists ?? false, seed = RimlinkSession.World?.Params?.Seed },
            players = state.Players.Online.Select(p => new { id = p.PlayerId, name = p.Name }).ToList(),
            settlements = state.Objects.All.Where(o => o.Owner is not null)
                .Select(o => new { owner = o.Owner.Name, ownerId = o.Owner.PlayerId, tile = o.Tile, name = o.Name }).ToList(),
            economy = new { balance = state.Economy.Balance, pendingParcels = state.Economy.Pending.Count, myLots = state.Economy.MyLots.Count },
            raids = new { incoming = state.Raids.Incoming.Count, mine = state.Raids.Mine.Count, peaceful = state.Raids.Flags.Peaceful },
            social = new
            {
                flags = new { acceptGifts = state.Raids.Flags.AcceptGifts, tradesClosed = state.Raids.Flags.TradesClosed },
                blocks = state.Social.Blocks.Select(b => b.Name).ToList(),
                awaitingGifts = state.Economy.Pending.Where(p => p.AwaitingDecision)
                    .Select(p => new { id = p.Id, from = p.From?.Name, items = p.Items.Items.Select(i => $"{i.DefName} x{i.Count}").ToList(), pawns = p.Pawns.Pawns.Select(x => x.Name).ToList(), silver = p.Silver }).ToList(),
                trades = state.Social.Trades.Select(t => new
                {
                    id = t.Id,
                    partner = (t.Initiator?.PlayerId == me ? t.Partner?.Name : t.Initiator?.Name),
                    status = t.Status.ToString(),
                    revision = t.Revision,
                    closeReason = t.CloseReason,
                    mine = SideOf(SocialTools.Mine(t)),
                    theirs = SideOf(SocialTools.Theirs(t)),
                }).ToList(),
            },
            live = new
            {
                invite = state.Live.PendingInvite?.Session.Id,
                sessionId = live?.Id,
                kind = live?.Kind.ToString(),
                state = live?.State.ToString(),
                role = live is null ? null : live.Host?.PlayerId == me ? "host" : "guest",
                // 0 none, 1 host (or waiting for the guest), 2 guest; as each side's HUD shows it.
                pausedBy = LiveHost.Active ? LiveTempo.PausedBy : LiveGuest.Session is not null ? MirrorMode.PausedBy : (int?)null,
                guestPausesUsed = LiveHost.Active ? LiveTempo.GuestPausesUsed : LiveGuest.Session is not null ? LiveGuest.PausesUsed : (int?)null,
            },
            lastPing = state.LastPing is { } ping ? new { from = ping.From?.Name, tile = ping.Tile, x = ping.Cell?.X, z = ping.Cell?.Z } : null,
            visit = VisitSession.InVisit,
            outbox = RimlinkSession.Outbox?.Entries.Count ?? 0,
        };
    }

    private static object SideOf(Protocol.V1.TradeSide side) => new
    {
        items = side.Items?.Items.Select(i => $"{i.DefName} x{i.Count}").ToList(),
        pawns = side.Pawns?.Pawns.Select(p => p.Name).ToList(),
        silver = side.Silver,
        note = side.Note,
        confirmed = side.Confirmed,
    };
}
