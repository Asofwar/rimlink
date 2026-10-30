using System.CommandLine;

namespace Rimlink.Tools.Commands;

/// <summary>`rimlink-tools admin &lt;command&gt; [args] --server URL --token T`: thin wrapper over the REST API.</summary>
public static class AdminCommand
{
    public static Command Build()
    {
        var server = new Option<string>("--server") { Description = "Server base URL, e.g. https://host:25560", Required = true };
        var token = new Option<string>("--token") { Description = "Admin:ApiToken", Required = true };
        var insecure = new Option<bool>("--insecure") { Description = "Accept a self-signed server certificate" };
        var name = new Argument<string>("command") { Description = "online | players [query] | player <playerId> | role <playerId> player|moderator|admin | rename <playerId> <name> [faction] | kick <playerId> [reason] | ban <playerId> [days] [reason] | pardon <banId> | bans | mute <playerId> <minutes> | unmute <playerId> | saves <playerId> | rollback <playerId> <version> | reset <playerId> | broadcast <text> | say <author> <text> | saveall | shutdown <seconds> [reason] | recreateworld | modpack | actions | chat [playerId] | status | config | backups | backup | backup-get <name> <file> | wallet <playerId> [+n|-n] [reason] | parcels <playerId> | lots [playerId] | refund lot <id> | refund parcel <id> | economy freeze [reason]|unfreeze|stats | raids list [playerId]|cancel <id> [reason]|freeze [reason]|unfreeze|stats | live list [playerId]|end <id> [reason] | peaceful <playerId> on|off | shop <playerId> | removals <playerId> | trades [playerId] | trade cancel <id> [reason] | player-freeze <playerId> [reason] | player-unfreeze <playerId> | forbid list|add|remove <def...> | archived [archived|restored|all] | archive <playerId> | restore-colony <archiveId> | incidents list [playerId]|cancel <id> [reason]" };
        var args = new Argument<string[]>("args") { Arity = ArgumentArity.ZeroOrMore };

        var command = new Command("admin", "Run an admin operation on a running server") { server, token, insecure, name, args };
        command.SetAction(async parse =>
        {
            using var rest = new RestClient(parse.GetValue(server)!, parse.GetValue(token)!, parse.GetValue(insecure));
            Console.WriteLine(await Run(rest, parse.GetValue(name)!, parse.GetValue(args) ?? []));
        });
        return command;
    }

    /// <summary>One admin command against the REST API; returns what the CLI prints.</summary>
    public static async Task<string> Run(RestClient rest, string command, string[] a)
    {
        string rest1 = string.Join(' ', a.Skip(1));
        return command switch
            {
                "online" => await rest.Get("players/online"),
                "players" => await rest.Get(a.Length > 0 ? $"players?q={Uri.EscapeDataString(string.Join(' ', a))}" : "players"),
                "player" => await rest.Get($"players/{a[0]}"),
                "role" => await rest.Post($"players/{a[0]}/role", new { role = a[1] }),
                "rename" => await rest.Post($"players/{a[0]}/rename", new { name = a[1], factionName = a.Length > 2 ? string.Join(' ', a.Skip(2)) : null }),
                "saveall" => await rest.Post("saveall", null),
                "chat" => await rest.Get(a.Length > 0 ? $"chat?playerId={a[0]}" : "chat"),
                "status" => await rest.Get("status"),
                "config" => await rest.Get("config"),
                "backups" => await rest.Get("backups"),
                "backup" => await rest.Post("backups", null),
                "backup-get" => await Download(rest, a[0], a[1]),
                "kick" => await rest.Post($"players/{a[0]}/kick", new { reason = rest1 }),
                "ban" => await rest.Post("bans", a.Length > 1 && double.TryParse(a[1], System.Globalization.CultureInfo.InvariantCulture, out double days)
                    ? new { playerId = long.Parse(a[0]), days = (double?)days, reason = string.Join(' ', a.Skip(2)) }
                    : new { playerId = long.Parse(a[0]), days = (double?)null, reason = rest1 }),
                "pardon" => await rest.Delete($"bans/{a[0]}"),
                "bans" => await rest.Get("bans"),
                "mute" => await rest.Post($"players/{a[0]}/mute", new { minutes = double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) }),
                "unmute" => await rest.Delete($"players/{a[0]}/mute"),
                "saves" => await rest.Get($"players/{a[0]}/saves"),
                "rollback" => await rest.Post($"players/{a[0]}/saves/{a[1]}/rollback", null),
                "reset" => await rest.Post($"players/{a[0]}/reset", null),
                "broadcast" => await rest.Post("broadcast", new { text = string.Join(' ', a) }),
                "say" => await rest.Post("chat", new { author = a[0], text = rest1 }),
                "shutdown" => await rest.Post("shutdown", new { inSeconds = int.Parse(a[0]), reason = rest1 }),
                "recreateworld" => await rest.Post("world/recreate", null),
                "modpack" => await rest.Get("modpack"),
                "actions" => await rest.Get("actions"),
                "wallet" => await rest.Post($"economy/wallet/{a[0]}", new { delta = long.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), reason = string.Join(' ', a.Skip(2)) }),
                "parcels" => await rest.Get($"economy/parcels/{a[0]}"),
                "lots" => await rest.Get(a.Length > 0 ? $"economy/lots?playerId={a[0]}" : "economy/lots"),
                "refund" => await rest.Post($"economy/refund/{a[0]}/{a[1]}", null),
                "trades" => await rest.Get(a.Length > 0 ? $"trades?playerId={a[0]}" : "trades"),
                "trade" => a[0] switch
                {
                    "cancel" => await rest.Post($"trades/{a[1]}/cancel", new { reason = string.Join(' ', a.Skip(2)) is { Length: > 0 } r ? r : "cancelled by admin" }),
                    _ => throw new InvalidOperationException("trade cancel <id> [reason]"),
                },
                "raids" => a[0] switch
                {
                    "list" => await rest.Get(a.Length > 1 ? $"raids?playerId={a[1]}" : "raids"),
                    "cancel" => await rest.Post($"raids/{a[1]}/cancel", new { reason = string.Join(' ', a.Skip(2).Where(x => x != "--return")), returnPawns = a.Contains("--return") }),
                    "freeze" => await rest.Post("raids/freeze", new { frozen = true, reason = rest1 }),
                    "unfreeze" => await rest.Post("raids/freeze", new { frozen = false, reason = (string?)null }),
                    "stats" => await rest.Get("raids/stats"),
                    _ => throw new InvalidOperationException("raids list [playerId] | cancel <id> [reason] | freeze [reason] | unfreeze | stats"),
                },
                "live" => a[0] switch
                {
                    "list" => await rest.Get(a.Length > 1 ? $"live?playerId={a[1]}" : "live"),
                    "end" => await rest.Post($"live/{a[1]}/end", new { reason = string.Join(' ', a.Skip(2)) }),
                    _ => throw new InvalidOperationException("live list [playerId] | end <id> [reason]"),
                },
                "archived" => await rest.Get(a.Length > 0 ? $"archive?state={a[0]}" : "archive"),
                "archive" => await rest.Post($"players/{a[0]}/archive", null),
                "restore-colony" => await rest.Post($"archive/{a[0]}/restore", null),
                "shop" => await rest.Get($"shop/{a[0]}"),
                "removals" => await rest.Get($"shop/{a[0]}/removals"),
                "incidents" => a[0] switch
                {
                    "list" => await rest.Get(a.Length > 1 ? $"incidents?playerId={a[1]}" : "incidents"),
                    "cancel" => await rest.Post($"incidents/{a[1]}/cancel", new { reason = a.Length > 2 ? string.Join(' ', a.Skip(2)) : null }),
                    _ => throw new InvalidOperationException("incidents list [playerId] | cancel <id> [reason]"),
                },
                "forbid" => a.Length == 0 || a[0] == "list"
                    ? await rest.Get("economy/forbidden-defs")
                    : await rest.Put("economy/forbidden-defs", new { defs = Forbid(await rest.Get("economy/forbidden-defs"), a) }),
                "peaceful" => await rest.Post($"players/{a[0]}/peaceful", new { peaceful = a[1] == "on" }),
                "player-freeze" => await rest.Post($"players/{a[0]}/economy", new { frozen = true, reason = a.Length > 1 ? rest1 : null }),
                "player-unfreeze" => await rest.Post($"players/{a[0]}/economy", new { frozen = false, reason = (string?)null }),
                "economy" => a[0] switch
                {
                    "freeze" => await rest.Post("economy/freeze", new { frozen = true, reason = rest1 }),
                    "unfreeze" => await rest.Post("economy/freeze", new { frozen = false, reason = (string?)null }),
                    "stats" => await rest.Get("economy/stats"),
                    _ => throw new InvalidOperationException("economy freeze [reason] | unfreeze | stats"),
                },
                var other => throw new InvalidOperationException($"unknown command '{other}'"),
            };
    }

    private static async Task<string> Download(RestClient rest, string name, string file)
    {
        await rest.Download($"backups/{Uri.EscapeDataString(name)}", file);
        return $"saved {file}";
    }

    /// <summary>The forbidden list after "add" or "remove" of the given defs, from the server's current list.</summary>
    private static string[] Forbid(string currentJson, string[] a)
    {
        var current = System.Text.Json.JsonDocument.Parse(currentJson).RootElement.GetProperty("defs").EnumerateArray().Select(e => e.GetString()!).ToList();
        return a[0] switch
        {
            "add" => current.Concat(a.Skip(1)).ToArray(),
            "remove" => current.Except(a.Skip(1), StringComparer.Ordinal).ToArray(),
            _ => throw new InvalidOperationException("forbid list | add <def...> | remove <def...>"),
        };
    }
}
