using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Admin;

/// <summary>Writes the audit log (<c>admin_actions</c>) and mirrors each entry to the admin feed.</summary>
public sealed class AdminAudit(RimlinkDbContext db, AdminFeed feed, ILogger<AdminAudit> log)
{
    public async Task Write(Actor actor, string action, long? targetAccountId, string details)
    {
        var row = Stage(actor, action, targetAccountId, details);
        await db.SaveChangesAsync();
        await Publish(row);
    }

    public AdminAction Stage(Actor actor, string action, long? targetAccountId, string details)
    {
        var row = new AdminAction { ActorAccountId = actor.AccountId, ActorName = actor.Name, Action = action, TargetAccountId = targetAccountId, Details = details, At = DateTime.UtcNow };
        db.AdminActions.Add(row);
        return row;
    }

    /// <summary>Call after the caller's transaction committed; staging never emits a premature feed entry.</summary>
    public async Task Publish(AdminAction row)
    {
        log.LogInformation("Admin {Actor}: {Action} {Target} {Details}", row.ActorName, row.Action, row.TargetAccountId, row.Details);
        FeedPlayer? target = row.TargetAccountId is { } accountId
            ? await db.Players.AsNoTracking().Where(p => p.AccountId == accountId).Select(p => new FeedPlayer(p.Id, p.Name)).FirstOrDefaultAsync()
            : null;
        feed.Publish(FeedTypes.AdminAction, new AdminActionData(row.Action, row.ActorName ?? "", target, row.Details));
    }
}
