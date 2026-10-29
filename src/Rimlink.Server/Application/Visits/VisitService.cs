using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Hosting;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Visit = Rimlink.Server.Storage.Entities.Visit;
using Rimlink.Server.Application.Feed;

namespace Rimlink.Server.Application.Visits;

/// <summary>A visit is a ticket: while it is open the visitor may download the host's save and buy from their shop.</summary>
public sealed class VisitService(RimlinkDbContext db, SaveStore saves, EventBus events, AdminFeed feed, IOptions<RaidOptions> options, ILogger<VisitService> log)
{
    /// <summary>A ticket nobody ended (the visitor's game crashed mid-visit) is good for Raids:VisitHours, then no longer opens the host's save or shop.</summary>
    private DateTime OpenedAfter => DateTime.UtcNow - TimeSpan.FromHours(options.Value.VisitHours);

    public async Task<(Visit Visit, Player Host)> Begin(long visitorId, long hostId)
    {
        if (hostId == visitorId) throw RpcErrors.Failed(ErrorCode.SelfTrade, "you cannot visit yourself");
        var host = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == hostId) ?? throw RpcErrors.Failed(ErrorCode.RecipientUnknown, "no such player");
        if (host.VisitsClosed) throw RpcErrors.Failed(ErrorCode.VisitsClosed, $"{host.Name} does not receive visitors");
        if (await db.LiveSessions.AnyAsync(s => !LiveSessionService.FinalStates.Contains(s.State) && (s.GuestPlayerId == visitorId || s.HostPlayerId == visitorId)))
            throw RpcErrors.Failed(ErrorCode.LiveActive, "you are in a live session");
        if (!await saves.HasSave(hostId)) throw new RpcException(new Status(StatusCode.NotFound, $"{host.Name} has no colony on the server yet"));

        var now = DateTime.UtcNow;
        await db.Visits.Where(v => v.VisitorPlayerId == visitorId && v.EndedAt == null).ExecuteUpdateAsync(s => s.SetProperty(v => v.EndedAt, now));
        var visit = new Visit { VisitorPlayerId = visitorId, HostPlayerId = hostId, StartedAt = now };
        db.Visits.Add(visit);
        await db.SaveChangesAsync();

        RaidMetrics.Visits.Inc();
        var visitor = await db.Players.AsNoTracking().FirstAsync(p => p.Id == visitorId);
        events.SendTo(hostId, new ServerEvent { VisitStarted = new VisitStarted { Visitor = visitor.ToRef() } });
        feed.Publish(FeedTypes.VisitStarted, new VisitData(new FeedPlayer(visitor.Id, visitor.Name), new FeedPlayer(host.Id, host.Name)));
        log.LogInformation("Player {Visitor} is visiting player {Host} (visit {Visit})", visitorId, hostId, visit.Id);
        return (visit, host);
    }

    public async Task End(long visitorId, long visitId)
    {
        await db.Visits.Where(v => v.Id == visitId && v.VisitorPlayerId == visitorId && v.EndedAt == null).ExecuteUpdateAsync(s => s.SetProperty(v => v.EndedAt, DateTime.UtcNow));
    }

    /// <summary>An open visit ticket, or an open live session (3b) where the player is the guest: both let them download the host's save and buy from the shop.</summary>
    public async Task<bool> IsVisiting(long visitorId, long hostId)
    {
        var since = OpenedAfter;
        if (await db.Visits.AnyAsync(v => v.VisitorPlayerId == visitorId && v.HostPlayerId == hostId && v.EndedAt == null && v.StartedAt > since)) return true;
        return await db.LiveSessions.AnyAsync(s => s.GuestPlayerId == visitorId && s.HostPlayerId == hostId
            && (s.State == LiveStateDb.Starting || s.State == LiveStateDb.Running || s.State == LiveStateDb.Waiting));
    }

    /// <summary>The player's colony is gone: nobody can keep visiting it, and their own visit is over.</summary>
    public async Task EndAllOf(long playerId)
    {
        await db.Visits.Where(v => (v.HostPlayerId == playerId || v.VisitorPlayerId == playerId) && v.EndedAt == null).ExecuteUpdateAsync(s => s.SetProperty(v => v.EndedAt, DateTime.UtcNow));
    }
}
