using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Admin;

public sealed record OnlinePlayer(long PlayerId, long AccountId, string Name, string FactionName, Role Role, string Ip, DateTime Since);

/// <summary>Every administrative operation, with the role check and the audit log in one place. gRPC, REST and chat commands all call this.</summary>
public sealed class AdminOperations(
    RimlinkDbContext db,
    SessionRegistry sessions,
    EventBus events,
    BanService bans,
    ChatService chat,
    SaveStore saves,
    DataDirectory data,
    IHostApplicationLifetime lifetime,
    ILogger<AdminOperations> log)
{
    public Task<List<OnlinePlayer>> ListOnline(Actor actor)
    {
        actor.Require(Role.Moderator);
        return Task.FromResult(sessions.All.Select(s => new OnlinePlayer(s.PlayerId, s.AccountId, s.PlayerName, "", s.Role, s.Ip, s.StartedAt)).OrderBy(p => p.Since).ToList());
    }

    public async Task<Player> FindPlayer(long playerId) =>
        await db.Players.Include(p => p.Account).FirstOrDefaultAsync(p => p.Id == playerId) ?? throw new RpcException(new Status(StatusCode.NotFound, $"no player with id {playerId}"));

    public async Task<Player> FindPlayerByAccount(long accountId) =>
        await db.Players.Include(p => p.Account).FirstOrDefaultAsync(p => p.AccountId == accountId) ?? throw new RpcException(new Status(StatusCode.NotFound, $"no player for account {accountId}"));

    public async Task<Player> FindPlayerByName(string name) =>
        await db.Players.Include(p => p.Account).FirstOrDefaultAsync(p => p.Name.ToLower() == name.ToLower()) ?? throw new RpcException(new Status(StatusCode.NotFound, $"no player named '{name}'"));

    public async Task<string> Kick(Actor actor, long playerId, string reason)
    {
        actor.Require(Role.Moderator);
        var player = await FindPlayer(playerId);
        var session = sessions.ByPlayer(playerId);
        if (session is null) return $"{player.Name} is not online";
        if (session.Role > actor.Role) throw RpcErrors.PermissionDenied("cannot kick someone with a higher role");

        events.SendTo(playerId, new ServerEvent { Notice = new Notice { Text = $"You were kicked: {reason}" } });
        sessions.End(session.Token);
        await Audit(actor, "kick", player.AccountId, reason);
        return $"{player.Name} was kicked ({reason})";
    }

    public async Task<Ban> Ban(Actor actor, long? playerId, long? steamId, string? ip, string reason, TimeSpan? duration)
    {
        actor.Require(Role.Admin);
        long? accountId = null;
        if (playerId is { } id)
        {
            var player = await FindPlayer(id);
            accountId = player.AccountId;
            steamId ??= player.Account.SteamId;
        }
        var ban = await bans.Ban(new BanRequest(accountId, steamId, ip, reason, duration, actor.AccountId));
        await Audit(actor, "ban", accountId, $"ban #{ban.Id}: {reason}" + (duration is { } d ? $" for {d.TotalDays:0.#} days" : " permanently"));
        return ban;
    }

    public async Task<bool> Pardon(Actor actor, long banId)
    {
        actor.Require(Role.Admin);
        bool ok = await bans.Pardon(banId);
        if (ok) await Audit(actor, "pardon", null, $"ban #{banId}");
        return ok;
    }

    public Task<List<Ban>> ListBans(Actor actor)
    {
        actor.Require(Role.Moderator);
        return bans.ListActive();
    }

    public async Task Mute(Actor actor, long playerId, TimeSpan duration)
    {
        actor.Require(Role.Moderator);
        var player = await FindPlayer(playerId);
        await chat.Mute(playerId, duration);
        await Audit(actor, "mute", player.AccountId, $"{duration.TotalMinutes:0} min");
    }

    public async Task Unmute(Actor actor, long playerId)
    {
        actor.Require(Role.Moderator);
        var player = await FindPlayer(playerId);
        await chat.Unmute(playerId);
        await Audit(actor, "unmute", player.AccountId, "");
    }

    public async Task<List<SaveVersion>> ListSaves(Actor actor, long playerId)
    {
        actor.Require(Role.Admin);
        await FindPlayer(playerId);
        return await saves.List(playerId);
    }

    public async Task<bool> RollbackSave(Actor actor, long playerId, int version)
    {
        actor.Require(Role.Admin);
        var player = await FindPlayer(playerId);
        bool ok = await saves.Rollback(playerId, version);
        if (ok) await Audit(actor, "rollback", player.AccountId, $"to v{version}");
        return ok;
    }

    /// <summary>Kicks the player, archives their saves and removes their world objects; the account stays.</summary>
    public async Task ResetPlayer(Actor actor, long playerId)
    {
        actor.Require(Role.Admin);
        var player = await FindPlayer(playerId);
        if (sessions.ByPlayer(playerId) is { } session)
        {
            events.SendTo(playerId, new ServerEvent { Notice = new Notice { Text = "Your colony was reset by an admin" } });
            sessions.End(session.Token);
        }

        string archive = Path.Combine(data.Archive, $"{playerId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        await saves.Archive(playerId, archive);
        await RemoveObjectsOf(playerId);
        await Audit(actor, "reset", player.AccountId, archive);
        log.LogInformation("Player {Name} reset by {Actor}", player.Name, actor.Name);
    }

    public async Task Broadcast(Actor actor, string text)
    {
        actor.Require(Role.Moderator);
        events.Broadcast(new ServerEvent { Notice = new Notice { Text = text } });
        await chat.System($"[server] {text}");
        await Audit(actor, "broadcast", null, text);
    }

    public async Task SetRole(Actor actor, long playerId, Role role)
    {
        actor.Require(Role.Admin);
        var player = await FindPlayer(playerId);
        player.Account.Role = role;
        await db.SaveChangesAsync();
        await Audit(actor, "role", player.AccountId, role.ToString());
    }

    /// <summary>Players may rename themselves; admins may rename anyone.</summary>
    public async Task RenamePlayer(Actor actor, long playerId, string name, string? factionName)
    {
        if (actor.PlayerId != playerId) actor.Require(Role.Admin);
        if (!Names.IsValid(name)) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.NameInvalid, "player name is not a valid name");
        if (factionName is not null && !Names.IsValid(factionName)) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.NameInvalid, "faction name is not a valid name");

        var player = await FindPlayer(playerId);
        if (await db.Players.AnyAsync(p => p.Name == name && p.Id != playerId))
            throw new RpcException(new Status(StatusCode.AlreadyExists, $"the name '{name}' is already taken"));
        player.Name = name;
        if (factionName is not null) player.FactionName = factionName;
        await db.SaveChangesAsync();
        await Audit(actor, "rename", player.AccountId, $"{name} / {player.FactionName}");
    }

    public async Task SaveAll(Actor actor)
    {
        actor.Require(Role.Moderator);
        events.Broadcast(new ServerEvent { Notice = new Notice { Text = "Please save your game now" } });
        await Audit(actor, "saveall", null, "");
    }

    public async Task Shutdown(Actor actor, int inSeconds, string reason)
    {
        actor.Require(Role.Admin);
        inSeconds = Math.Max(0, inSeconds);
        events.Broadcast(new ServerEvent { Shutdown = new ServerShutdown { InSeconds = inSeconds, Reason = reason } });
        await Audit(actor, "shutdown", null, $"in {inSeconds}s: {reason}");
        log.LogWarning("Shutdown in {Seconds}s requested by {Actor}: {Reason}", inSeconds, actor.Name, reason);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(inSeconds) + TimeSpan.FromMilliseconds(200));
            lifetime.StopApplication();
        });
    }

    /// <summary>Archives every save and world object and forgets the world; everyone is kicked. The next admin login creates a new world.</summary>
    public async Task RecreateWorld(Actor actor)
    {
        actor.Require(Role.Admin);
        string archive = Path.Combine(data.Archive, $"world-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(archive);

        foreach (var session in sessions.All)
        {
            events.SendTo(session.PlayerId, new ServerEvent { Notice = new Notice { Text = "The world is being recreated; please reconnect later" } });
            sessions.End(session.Token);
        }
        foreach (long playerId in await db.SaveVersions.Select(v => v.PlayerId).Distinct().ToListAsync())
            await saves.Archive(playerId, Path.Combine(archive, playerId.ToString()));
        await db.WorldObjects.ExecuteDeleteAsync();
        await db.World.ExecuteDeleteAsync();
        await Audit(actor, "recreateworld", null, archive);
        log.LogWarning("World recreated by {Actor}; old data in {Archive}", actor.Name, archive);
    }

    public Task<List<AdminAction>> ListActions(Actor actor, int limit = 100)
    {
        actor.Require(Role.Moderator);
        return db.AdminActions.AsNoTracking().OrderByDescending(a => a.Id).Take(limit).ToListAsync();
    }

    private async Task RemoveObjectsOf(long playerId)
    {
        var objects = await db.WorldObjects.Where(o => o.OwnerPlayerId == playerId).ToListAsync();
        db.WorldObjects.RemoveRange(objects);
        await db.SaveChangesAsync();
        foreach (var o in objects) events.Broadcast(new ServerEvent { WorldObjectRemoved = new WorldObjectRemoved { ObjectId = o.Id } });
    }

    private async Task Audit(Actor actor, string action, long? targetAccountId, string details)
    {
        db.AdminActions.Add(new AdminAction { ActorAccountId = actor.AccountId, Action = action, TargetAccountId = targetAccountId, Details = details, At = DateTime.UtcNow });
        await db.SaveChangesAsync();
        log.LogInformation("Admin {Actor}: {Action} {Target} {Details}", actor.Name, action, targetAccountId, details);
    }
}
