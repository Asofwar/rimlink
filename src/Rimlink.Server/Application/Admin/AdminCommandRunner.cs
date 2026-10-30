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
        shutdown <seconds> [reason] | recreateworld | modpack | actions | help |
        wallet <player> [+n|-n] [reason] | parcels <player> | lots [player] | refund lot <id> | refund parcel <id> |
        economy freeze [reason] | economy unfreeze | economy stats |
        raids list [player] | raids cancel <id> [--return] [reason] | raids freeze [reason] | raids unfreeze | raids stats |
        live list [player] | live end <id> [reason] | peaceful <player> on|off | shop <player> | removals <player> |
        trades [player] | trade cancel <id> [reason] |
        freeze <player> [reason] | unfreeze <player> |
        forbid list | forbid add <def...> | forbid remove <def...> |
        archive list | archive <player> | archive restore <id> |
        incidents list [player] | incidents cancel <id> [reason]
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
                return saves.Count == 0 ? "no saves" : string.Join('\n', saves.Select(s => $"v{s.Number}{(s.IsCurrent ? " (current)" : "")}{(s.DevMode ? " dev" : "")} tick {s.GameTick} {s.Size / 1024} KB {s.CreatedAt:u}"));
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
            case "wallet":
            {
                long id = await PlayerId(Arg(0));
                if (args.Count < 2) return $"{Arg(0)}: {await ops.WalletBalance(actor, id)} silver";
                long delta = long.Parse(Arg(1), CultureInfo.InvariantCulture);
                long balance = await ops.AdjustWallet(actor, id, delta, args.Count > 2 ? Rest(2) : "admin adjustment");
                return $"{Arg(0)}: {balance} silver";
            }
            case "parcels":
            {
                var parcels = await ops.ListParcels(actor, await PlayerId(Arg(0)));
                return parcels.Count == 0 ? "no parcels" : string.Join('\n', parcels.Select(p => $"#{p.Id} {p.Kind} from {p.FromPlayerId?.ToString() ?? "-"} silver {p.Silver} {p.SizeBytes} B {(p.DeliveredAt is null ? "pending" : $"delivered {p.DeliveredAt:u}")}"));
            }
            case "lots":
            {
                var lots = await ops.ListLots(actor, args.Count > 0 ? await PlayerId(Arg(0)) : null);
                return lots.Count == 0 ? "no open lots" : string.Join('\n', lots.Select(l => $"#{l.Id} seller {l.SellerPlayerId} price {l.Price} expires {l.ExpiresAt:u}"));
            }
            case "refund":
            {
                long id = long.Parse(Arg(1), CultureInfo.InvariantCulture);
                bool ok = Arg(0) switch
                {
                    "lot" => await ops.RefundLot(actor, id),
                    "parcel" => await ops.RefundParcel(actor, id),
                    _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "refund lot <id> | refund parcel <id>")),
                };
                return ok ? $"{Arg(0)} {id} refunded" : $"{Arg(0)} {id} not found or not refundable";
            }
            case "forbid":
            {
                var current = ops.ListForbiddenDefs(actor);
                var defs = Arg(0) switch
                {
                    "list" => current,
                    "add" => await ops.SetForbiddenDefs(actor, current.Concat(args.Skip(1))),
                    "remove" => await ops.SetForbiddenDefs(actor, current.Except(args.Skip(1), StringComparer.Ordinal)),
                    _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "forbid list | forbid add <def...> | forbid remove <def...>")),
                };
                return defs.Count == 0 ? "no defs are forbidden" : string.Join(' ', defs);
            }
            case "economy":
                switch (Arg(0))
                {
                    case "freeze": await ops.SetEconomyFrozen(actor, true, args.Count > 1 ? Rest(1) : "maintenance"); return "trading paused";
                    case "unfreeze": await ops.SetEconomyFrozen(actor, false, null); return "trading open";
                    case "stats":
                        var s = await ops.EconomyStats(actor);
                        return $"silver in wallets {s.SilverInWallets}, open lots {s.OpenLots}, pending parcels {s.PendingParcels}, trades today {s.TradesToday}, storage fees {s.StorageFeesCollected}{(s.Frozen ? $", FROZEN: {s.FreezeReason}" : "")}";
                    default: throw new RpcException(new Status(StatusCode.InvalidArgument, "economy freeze [reason] | unfreeze | stats"));
                }
            case "trades":
            {
                var list = await ops.ListTrades(actor, args.Count > 0 ? await PlayerId(Arg(0)) : null);
                return list.Count == 0 ? "no trades" : string.Join('\n', list.Select(t =>
                    $"#{t.Id} {t.InitiatorName} <-> {t.PartnerName} {t.Status.ToLowerInvariant()} r{t.Revision}: [{t.InitiatorSide}] for [{t.PartnerSide}]{(t.CloseReason is null ? "" : $" ({t.CloseReason})")}"));
            }
            case "trade":
                if (Arg(0) != "cancel") throw new RpcException(new Status(StatusCode.InvalidArgument, "trade cancel <id> [reason]"));
                return await ops.CancelTrade(actor, long.Parse(Arg(1), CultureInfo.InvariantCulture), args.Count > 2 ? Rest(2) : "cancelled by admin") ? "trade cancelled" : "no such open trade";
            case "raids":
                switch (Arg(0))
                {
                    case "list":
                        var list = await ops.ListRaids(actor, args.Count > 1 ? await PlayerId(Arg(1)) : null);
                        return list.Count == 0 ? "no raids" : string.Join("\n", list.Select(r => $"#{r.Id} {r.AttackerPlayerId}->{r.TargetPlayerId} {r.State} {(PawnMode)r.PawnMode}/{(RaidTiming)r.Timing} created {r.CreatedAt:u}"));
                    case "cancel":
                    {
                        // raids cancel <id> [--return] [reason]: --return also sends a landed raid's pawns home.
                        bool returnPawns = args.Count > 2 && args[2] == "--return";
                        int reasonAt = returnPawns ? 3 : 2;
                        return await ops.CancelRaid(actor, long.Parse(Arg(1)), args.Count > reasonAt ? Rest(reasonAt) : "cancelled by admin", returnPawns) ? "raid cancelled" : "no such active raid";
                    }
                    case "freeze": await ops.SetRaidsFrozen(actor, true, args.Count > 1 ? Rest(1) : "maintenance"); return "raids paused";
                    case "unfreeze": await ops.SetRaidsFrozen(actor, false, null); return "raids open";
                    case "stats":
                        var rs = await ops.RaidStats(actor);
                        return $"queued {rs.Queued}, announced {rs.Announced}, landed {rs.Landed}, resolved today {rs.ResolvedToday}, expired {rs.Expired}, cancelled {rs.Cancelled}{(rs.Frozen ? $", FROZEN: {rs.FreezeReason}" : "")}";
                    default: throw new RpcException(new Status(StatusCode.InvalidArgument, "raids list [player] | cancel <id> [reason] | freeze [reason] | unfreeze | stats"));
                }
            case "archive":
                switch (Arg(0))
                {
                    case "list":
                    {
                        var rows = await ops.ListArchived(actor, "archived");
                        return rows.Count == 0 ? "no archived colonies" : string.Join('\n', rows.Select(r =>
                            $"#{r.Id} {r.PlayerName}: '{r.Colony}' tile {r.Tile}, archived {r.ArchivedAt:u} ({r.Reason} by {r.ArchivedBy})"));
                    }
                    case "restore":
                        return await ops.RestoreColony(actor, long.Parse(Arg(1), CultureInfo.InvariantCulture)) switch
                        {
                            Rimlink.Server.Application.World.RestoreOutcome.Restored r => $"'{r.Settlement.Name}' restored on tile {r.Settlement.Tile}",
                            Rimlink.Server.Application.World.RestoreOutcome.NotFound => "no such archive",
                            Rimlink.Server.Application.World.RestoreOutcome.AlreadyRestored => "already restored",
                            Rimlink.Server.Application.World.RestoreOutcome.PlayerHasColony => "the player has a colony now",
                            Rimlink.Server.Application.World.RestoreOutcome.TileTaken => "the tile is taken",
                            _ => "the archive is damaged",
                        };
                    default:
                    {
                        string name = Rest(0);
                        var outcome = await ops.ArchiveColony(actor, await PlayerId(name));
                        if (outcome is Rimlink.Server.Application.World.ArchiveOutcome.PlayerOnline) return $"{name} is online: only offline players are archived";
                        if (outcome is not Rimlink.Server.Application.World.ArchiveOutcome.Archived archived) return $"{name} has no colony";
                        var row = (await ops.ListArchived(actor, "archived")).First(r => r.Id == archived.ArchiveId);
                        return $"'{row.Colony}' of {row.PlayerName} archived as #{row.Id}";
                    }
                }
            case "incidents":
                switch (Arg(0))
                {
                    case "list":
                        var incidents = await ops.ListIncidents(actor, args.Count > 1 ? await PlayerId(Arg(1)) : null);
                        return incidents.Count == 0 ? "no incidents" : string.Join("\n", incidents.Select(i => $"#{i.Id} {i.BuyerPlayerId}->{i.TargetPlayerId} {i.EntryId} L{i.Level}{(i.Pods ? " pods" : "")} {i.Price} silver {i.State}{(i.Reason is { } r ? ": " + r : "")} created {i.CreatedAt:u}"));
                    case "cancel":
                        return await ops.CancelIncident(actor, long.Parse(Arg(1)), args.Count > 2 ? Rest(2) : "cancelled by admin") ? "incident cancelled" : "no such waiting incident";
                    default: throw new RpcException(new Status(StatusCode.InvalidArgument, "incidents list [player] | cancel <id> [reason]"));
                }
            case "live":
                switch (Arg(0))
                {
                    case "list":
                        var sessions = await ops.ListLive(actor, args.Count > 1 ? await PlayerId(Arg(1)) : null);
                        return sessions.Count == 0 ? "no live sessions" : string.Join("\n", sessions.Select(s => $"#{s.Id} {(LiveKind)s.Kind} host {s.HostPlayerId} guest {s.GuestPlayerId} {s.State}{(s.EndReason is { } er ? " " + (LiveEndReason)er : "")} created {s.CreatedAt:u}"));
                    case "end": return await ops.EndLive(actor, long.Parse(Arg(1)), args.Count > 2 ? Rest(2) : "ended by admin") ? "live session ending" : "no such live session";
                    default: throw new RpcException(new Status(StatusCode.InvalidArgument, "live list [player] | end <id> [reason]"));
                }
            case "shop":
                var shopRows = await ops.ListShop(actor, await PlayerId(Arg(0)));
                return shopRows.Count == 0 ? "nothing for sale" : string.Join("\n", shopRows.Select(s => $"#{s.ThingId} {s.Count}x {s.Label} @ {s.Price}{(s.SoldAt is not null ? " (sold)" : "")}"));
            case "removals":
                var removals = await ops.ListRemovals(actor, await PlayerId(Arg(0)));
                return removals.Count == 0 ? "nothing pending" : string.Join("\n", removals.Select(r => $"#{r.Id} thing {r.ThingId} x{r.Count} since {r.CreatedAt:u}"));
            case "peaceful":
                await ops.SetPeaceful(actor, await PlayerId(Arg(0)), Arg(1) == "on");
                return $"peaceful mode {Arg(1)} for {Arg(0)}";
            case "freeze":
                await ops.SetPlayerEconomyFrozen(actor, await PlayerId(Arg(0)), true, args.Count > 1 ? Rest(1) : null);
                return $"economy frozen for {Arg(0)}";
            case "unfreeze":
                await ops.SetPlayerEconomyFrozen(actor, await PlayerId(Arg(0)), false, null);
                return $"economy open for {Arg(0)}";
            case "actions":
                var actions = await ops.ListActions(actor, 30);
                return actions.Count == 0 ? "no actions yet" : string.Join('\n', actions.Select(a => $"{a.At:u} #{a.ActorAccountId} {a.Action} {a.TargetAccountId} {a.Details}"));
            default:
                return Help;
        }
    }

    private static string Usage(string name) => Help;
}
