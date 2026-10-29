using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Chat;

public sealed class ChatService(RimlinkDbContext db, EventBus events, AdminFeed feed, Social.SocialService social, SessionRegistry sessions, IServiceProvider services, ILogger<ChatService> log)
{
    public const int ExternalAuthorMaxLength = 32;
    /// <summary>Tile numbers above any planet's (a world of a few hundred thousand tiles) and map cells above any map's (250×250 by default).</summary>
    public const int MaxPingTile = 10_000_000, MaxPingCell = 1000;

    /// <summary>The game chat's rules for a line: trimmed, not empty, at most <see cref="Limits.ChatMaxLength"/>, no control characters but newlines.</summary>
    public static string CleanText(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new RpcException(new Status(StatusCode.InvalidArgument, "message is empty"));
        if (text.Length > Limits.ChatMaxLength) throw new RpcException(new Status(StatusCode.InvalidArgument, $"message longer than {Limits.ChatMaxLength} characters"));
        if (text.Any(c => char.IsControl(c) && c != '\n')) throw new RpcException(new Status(StatusCode.InvalidArgument, "message contains control characters"));
        return text;
    }

    public async Task Send(Session sender, string text, long toPlayerId)
    {
        text = CleanText(text);

        if (Admin.ChatCommandParser.Parse(text) is { } command)
        {
            // "/kick Nova ..." runs with the sender's role; the answer goes back only to the sender.
            var runner = services.GetRequiredService<Admin.AdminCommandRunner>();
            string answer = await runner.Execute(Admin.Actor.From(sender), command);
            events.SendTo(sender.PlayerId, new ServerEvent { Chat = new ChatMessageEvent { ToPlayerId = sender.PlayerId, Text = answer, Kind = Protocol.V1.MessageKind.System, At = Timestamp.FromDateTime(DateTime.UtcNow) } });
            return;
        }

        var from = await db.Players.AsNoTracking().FirstAsync(p => p.Id == sender.PlayerId);
        if (from.MutedUntil is { } until && until > DateTime.UtcNow)
            throw RpcErrors.Failed(ErrorCode.Muted, $"you are muted until {until:u}");
        if (toPlayerId != 0 && !await db.Players.AnyAsync(p => p.Id == toPlayerId))
            throw new RpcException(new Status(StatusCode.NotFound, "no such player"));
        if (toPlayerId != 0 && toPlayerId != from.Id) await Social.BlockRules.RequireNotBlocked(db, from.Id, toPlayerId);

        var message = new ChatMessage { FromPlayerId = from.Id, ToPlayerId = toPlayerId == 0 ? null : toPlayerId, Text = text, Kind = Storage.Entities.MessageKind.Player, SentAt = DateTime.UtcNow };
        await Persist(message, new FeedPlayer(from.Id, from.Name));

        var ev = new ServerEvent { Chat = ToEvent(message, from.ToRef()) };
        if (toPlayerId == 0)
        {
            // Players who blocked the sender do not get the line.
            var blockers = await social.BlockersOf(from.Id);
            if (blockers.Count == 0) events.Broadcast(ev);
            else events.BroadcastExcept(blockers, ev);
        }
        else
        {
            events.SendTo(toPlayerId, ev);
            if (toPlayerId != sender.PlayerId) events.SendTo(sender.PlayerId, ev);
        }
    }

    /// <summary>"Look here" for one online player (live UX spec §2): never stored, not in the history or the admin feed.</summary>
    public async Task Ping(Session sender, long toPlayerId, int tile, MapCell? cell)
    {
        if (tile is < 0 or >= MaxPingTile) throw new RpcException(new Status(StatusCode.InvalidArgument, $"tile must be 0-{MaxPingTile - 1}"));
        if (cell is not null && (cell.X is < 0 or >= MaxPingCell || cell.Z is < 0 or >= MaxPingCell))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"cell must be 0-{MaxPingCell - 1} on both axes"));
        var from = await db.Players.AsNoTracking().FirstAsync(p => p.Id == sender.PlayerId);
        if (from.MutedUntil is { } until && until > DateTime.UtcNow) throw RpcErrors.Failed(ErrorCode.Muted, $"you are muted until {until:u}");
        if (toPlayerId == sender.PlayerId || sessions.ByPlayer(toPlayerId) is null) throw RpcErrors.Failed(ErrorCode.RecipientUnknown, "that player is not online");
        events.SendTo(toPlayerId, new ServerEvent { Ping = new PingEvent { From = from.ToRef(), Tile = tile, Cell = cell } });
    }

    public async Task<List<ChatMessageEvent>> History(Session reader)
    {
        // Last 50 public lines (without those of players the reader blocked) plus the reader's last 50 direct messages, merged in order.
        var blocked = (await social.BlockedBy(reader.PlayerId)).ToList();
        var publicRows = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ToPlayerId == null && (m.FromPlayerId == null || !blocked.Contains(m.FromPlayerId.Value)))
            .OrderByDescending(m => m.Id).Take(Limits.ChatOnLogin).ToListAsync();
        var directRows = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ToPlayerId != null && (m.ToPlayerId == reader.PlayerId || m.FromPlayerId == reader.PlayerId))
            .OrderByDescending(m => m.Id).Take(Limits.ChatOnLogin).ToListAsync();
        var rows = publicRows.Concat(directRows).OrderBy(m => m.Id).ToList();

        var senderIds = rows.Where(m => m.FromPlayerId != null).Select(m => m.FromPlayerId!.Value).Distinct().ToList();
        var senders = await db.Players.AsNoTracking().Where(p => senderIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return rows.Select(m => ToEvent(m, m.FromPlayerId is { } id && senders.TryGetValue(id, out var p) ? p.ToRef() : null)).ToList();
    }

    /// <summary>
    /// A public line from outside the game (the Discord bot): stored with its author, sent to everyone as <c>EXTERNAL</c>. Never
    /// parsed as a <c>/</c> command. Returns the line's id.
    /// </summary>
    public async Task<long> External(string author, string text)
    {
        author = author.Trim();
        if (author.Length is 0 or > ExternalAuthorMaxLength || author.Any(char.IsControl))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"author must be 1-{ExternalAuthorMaxLength} characters without control characters"));
        var message = new ChatMessage { Text = CleanText(text), Kind = Storage.Entities.MessageKind.External, ExternalAuthor = author, SentAt = DateTime.UtcNow };
        await Persist(message, null);
        events.Broadcast(new ServerEvent { Chat = ToEvent(message, null) });
        return message.Id;
    }

    /// <summary>Server-generated line for everyone (join, leave, announcements).</summary>
    public async Task System(string text)
    {
        var message = new ChatMessage { Text = text, Kind = Storage.Entities.MessageKind.System, SentAt = DateTime.UtcNow };
        await Persist(message, null);
        events.Broadcast(new ServerEvent { Chat = ToEvent(message, null) });
    }

    public async Task Mute(long playerId, TimeSpan duration)
    {
        var player = await db.Players.FindAsync(playerId) ?? throw new RpcException(new Status(StatusCode.NotFound, "no such player"));
        player.MutedUntil = DateTime.UtcNow + duration;
        await db.SaveChangesAsync();
        log.LogInformation("Player {Name} muted until {Until}", player.Name, player.MutedUntil);
    }

    public async Task Unmute(long playerId)
    {
        var player = await db.Players.FindAsync(playerId) ?? throw new RpcException(new Status(StatusCode.NotFound, "no such player"));
        player.MutedUntil = null;
        await db.SaveChangesAsync();
    }

    private async Task Persist(ChatMessage message, FeedPlayer? from)
    {
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();
        if (message.ToPlayerId is null)
            feed.Publish(FeedTypes.ChatMessage, new ChatData(message.Id, message.Kind.ToString().ToLowerInvariant(), from, message.ExternalAuthor, message.Text));
        // Keep the table at ChatStored rows.
        long? cutoff = await db.ChatMessages.OrderByDescending(m => m.Id).Skip(Limits.ChatStored).Select(m => (long?)m.Id).FirstOrDefaultAsync();
        if (cutoff is { } c) await db.ChatMessages.Where(m => m.Id <= c).ExecuteDeleteAsync();
    }

    private static ChatMessageEvent ToEvent(ChatMessage m, PlayerRef? from) => new()
    {
        Id = m.Id,
        From = from,
        ToPlayerId = m.ToPlayerId ?? 0,
        Text = m.Text,
        Kind = m.Kind switch
        {
            Storage.Entities.MessageKind.System => Protocol.V1.MessageKind.System,
            Storage.Entities.MessageKind.External => Protocol.V1.MessageKind.External,
            _ => Protocol.V1.MessageKind.Player,
        },
        At = Timestamp.FromDateTime(DateTime.SpecifyKind(m.SentAt, DateTimeKind.Utc)),
        ExternalAuthor = m.ExternalAuthor ?? "",
    };
}
