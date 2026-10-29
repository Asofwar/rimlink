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
        db.AdminActions.Add(new AdminAction { ActorAccountId = actor.AccountId, ActorName = actor.Name, Action = action, TargetAccountId = targetAccountId, Details = details, At = DateTime.UtcNow });
        await db.SaveChangesAsync();
        log.LogInformation("Admin {Actor}: {Action} {Target} {Details}", actor.Name, action, targetAccountId, details);
        FeedPlayer? target = targetAccountId is { } accountId
            ? await db.Players.AsNoTracking().Where(p => p.AccountId == accountId).Select(p => new FeedPlayer(p.Id, p.Name)).FirstOrDefaultAsync()
            : null;
        feed.Publish(FeedTypes.AdminAction, new AdminActionData(action, actor.Name, target, details));
    }
}
