using System.Globalization;
using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Admin;

/// <summary>Text command front-end over <see cref="AdminOperations"/>, shared by the Admin gRPC service and chat "/" lines.</summary>
public sealed class AdminCommandRunner(AdminOperations ops, ModpackService modpack)
{
    public const string Help = """
        Commands: online | kick <player> [reason] | ban <player> [days] [reason] | pardon <banId> | bans |
        mute <player> <minutes> | unmute <player> | saves <player> | rollback <player> <version> | reset <player> |
        broadcast <text> | role <player> <player|moderator|admin> | rename <name> [faction] | saveall |
        shutdown <seconds> [reason] | recreateworld | modpack | actions | help
        """;

    public async Task<string> Execute(Actor actor, AdminCommand command)
    {
        var args = command.Args.ToList();
        string Arg(int i) => i < args.Count ? args[i] : throw new RpcException(new Status(StatusCode.InvalidArgument, $"missing argument {i + 1}; {Usage(command.Name)}"));
        string Rest(int from) => string.Join(' ', args.Skip(from));
        async Task<long> PlayerId(string name) => (await ops.FindPlayerByName(name)).Id;

        switch (command.Name)
        {
            case "online":
                var online = await ops.ListOnline(actor);
                return online.Count == 0 ? "nobody online" : string.Join('\n', online.Select(p => $"{p.Name} ({p.Role.ToString().ToLowerInvariant()}, since {p.Since:HH:mm} UTC)"));
            case "kick":
                return await ops.Kick(actor, await PlayerId(Arg(0)), args.Count > 1 ? Rest(1) : "kicked by a moderator");
            case "ban":
            {
                long id = await PlayerId(Arg(0));
                TimeSpan? duration = null;
                int reasonFrom = 1;
                if (args.Count > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double days)) { duration = TimeSpan.FromDays(days); reasonFrom = 2; }
                var ban = await ops.Ban(actor, id, null, null, args.Count > reasonFrom ? Rest(reasonFrom) : "banned", duration);
                return $"ban #{ban.Id} issued" + (duration is { } d ? $" for {d.TotalDays:0.#} days" : " permanently");
            }
            case "pardon":
                return await ops.Pardon(actor, long.Parse(Arg(0), CultureInfo.InvariantCulture)) ? "ban lifted" : "no such active ban";
            case "bans":
                var bans = await ops.ListBans(actor);
                return bans.Count == 0 ? "no active bans" : string.Join('\n', bans.Select(b => $"#{b.Id} account={b.AccountId} steam={b.SteamId} ip={b.Ip} {(b.ExpiresAt is null ? "permanent" : $"until {b.ExpiresAt:u}")}: {b.Reason}"));
            case "mute":
                await ops.Mute(actor, await PlayerId(Arg(0)), TimeSpan.FromMinutes(double.Parse(Arg(1), CultureInfo.InvariantCulture)));
                return $"{Arg(0)} muted for {Arg(1)} minutes";
            case "unmute":
                await ops.Unmute(actor, await PlayerId(Arg(0)));
                return $"{Arg(0)} unmuted";
            case "saves":
                var saves = await ops.ListSaves(actor, await PlayerId(Arg(0)));
                return saves.Count == 0 ? "no saves" : string.Join('\n', saves.Select(s => $"v{s.Number}{(s.IsCurrent ? " (current)" : "")} tick {s.GameTick} {s.Size / 1024} KB {s.CreatedAt:u}"));
            case "rollback":
                return await ops.RollbackSave(actor, await PlayerId(Arg(0)), int.Parse(Arg(1), CultureInfo.InvariantCulture)) ? $"{Arg(0)} rolled back to v{Arg(1)}" : "no such version";
            case "reset":
                await ops.ResetPlayer(actor, await PlayerId(Arg(0)));
                return $"{Arg(0)} reset; their saves are archived";
            case "broadcast":
                await ops.Broadcast(actor, Rest(0).Length > 0 ? Rest(0) : throw new RpcException(new Status(StatusCode.InvalidArgument, "broadcast needs a text")));
                return "sent";
            case "role":
                var role = Enum.Parse<Role>(Arg(1), ignoreCase: true);
                await ops.SetRole(actor, await PlayerId(Arg(0)), role);
                return $"{Arg(0)} is now {role.ToString().ToLowerInvariant()} (applies at next login)";
            case "rename":
                await ops.RenamePlayer(actor, actor.PlayerId, Arg(0), args.Count > 1 ? Rest(1) : null);
                return $"you are now {Arg(0)}";
            case "saveall":
                await ops.SaveAll(actor);
                return "asked everyone to save";
            case "shutdown":
                await ops.Shutdown(actor, int.Parse(Arg(0), CultureInfo.InvariantCulture), args.Count > 1 ? Rest(1) : "server shutdown");
                return $"shutting down in {Arg(0)} s";
            case "recreateworld":
                await ops.RecreateWorld(actor);
                return "world removed; the next admin login creates a new one";
            case "modpack":
                var entries = await modpack.Get();
                return entries.Count == 0 ? "modpack check is off (empty list)" : string.Join('\n', entries.Select(e => $"{e.Position}. {e.PackageId} {e.Version} {e.Kind.ToString().ToLowerInvariant()}"));
            case "actions":
                var actions = await ops.ListActions(actor, 30);
                return actions.Count == 0 ? "no actions yet" : string.Join('\n', actions.Select(a => $"{a.At:u} #{a.ActorAccountId} {a.Action} {a.TargetAccountId} {a.Details}"));
            default:
                return Help;
        }
    }

    private static string Usage(string name) => Help;
}
