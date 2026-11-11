using System.CommandLine;

namespace Rimlink.Tools.Commands;

/// <summary>`rimlink-tools admin &lt;command&gt; [args] --server URL --token T`: thin wrapper over the REST API.</summary>
public static partial class AdminCommand
{
    public static Command Build()
    {
        var server = new Option<string>("--server") { Description = "Server base URL, e.g. https://host:25560", Required = true };
        var token = new Option<string>("--token") { Description = "Admin:ApiToken", Required = true };
        var insecure = new Option<bool>("--insecure") { Description = "Accept a self-signed server certificate" };
        var name = new Argument<string>("command") { Description = "online | players [query] | player <playerId> | role <playerId> player|moderator|admin | rename <playerId> <name> [faction] | kick <playerId> [reason] | ban <playerId> [days] [reason] | pardon <banId> | bans | mute <playerId> <minutes> | unmute <playerId> | saves <playerId> | rollback <playerId> <version> | reset <playerId> | broadcast <text> | say <author> <text> | saveall | shutdown <seconds> [reason] | recreateworld | modpack | actions | chat [playerId] | status | config | backups | backup | backup-get <name> <file> | wallet <playerId> [+n|-n] [reason] | parcels <playerId> | lots [playerId] | refund lot <id> | refund parcel <id> | economy freeze [reason]|unfreeze|stats | raids list [playerId]|cancel <id> [reason]|freeze [reason]|unfreeze|stats | live list [playerId]|end <id> [reason] | peaceful <playerId> on|off | shop <playerId> | removals <playerId> | trades [playerId] | trade cancel <id> [reason] | player-freeze <playerId> [reason] | player-unfreeze <playerId> | forbid list|add|remove <def...> | archived [archived|restored|all] | archive <playerId> | restore-colony <archiveId> | incidents list [playerId]|cancel <id> [reason] | procurement list [page]|cancel <id> [reason]" };
        name.Description += " | auctions | auction cancel <id> [reason]";
        var args = new Argument<string[]>("args") { Arity = ArgumentArity.ZeroOrMore };
        name.Description += " | colonies [playerId] | colony revoke <ownerPlayerId> <memberPlayerId>";
        name.Description += " | outposts inspect [--player ID] [--operation ID] [--limit N] [--cursor VALUE] | outposts preview|initialize|activate-domain-format|resolve --request FILE | outposts manifest inspect ID|upload FILE|confirm --request FILE";
        name.Description += " | " + GuildCaptureUsage;
        name.Description += " | influence | influence-setting <settlementId> | influence-archive-setting <archiveId> | influence-disable <settlementId> <requestId> <worldId> <generation> <allianceId> <version> | influence-archive-disable <archiveId> <settlementId> <requestId> <worldId> <generation> <allianceId> <version> | influence-color <allianceId> <worldId> <paletteIndex> <requestId>";

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
                "outposts" => await RunOutposts(rest, a),
                "guild-capture" => await RunGuildCapture(rest, a),
                var influence when influence.StartsWith("influence", StringComparison.Ordinal) => await RunInfluence(rest, influence, a),
                "online" => await rest.Get("players/online"),
                "colonies" => await rest.Get(a.Length > 0 ? $"colonies?playerId={a[0]}" : "colonies"),
                "colony" when a.Length == 3 && a[0] == "revoke" => await rest.Delete($"colonies/{a[1]}/{a[2]}"),
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
                "auctions" => await rest.Get("auctions/"),
                "auction" => a[0] == "cancel"
                    ? await rest.Post($"auctions/{a[1]}/cancel", new { reason = a.Length > 2 ? string.Join(' ', a.Skip(2)) : "cancelled by admin" })
                    : throw new InvalidOperationException("auction cancel <id> [reason]"),
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
                "procurement" => a.Length == 0 || a[0] == "list"
                    ? await rest.Get(a.Length > 1 ? $"procurement?page={int.Parse(a[1])}" : "procurement")
                    : a[0] == "cancel" && a.Length > 1
                        ? await rest.Post($"procurement/{long.Parse(a[1])}/cancel", new { reason = string.Join(' ', a.Skip(2)) })
                        : throw new InvalidOperationException("procurement list [page] | cancel <id> [reason]"),
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

    private static async Task<string> RunInfluence(RestClient rest, string command, string[] args)
    {
        int expected = command switch { "influence" => 0, "influence-setting" or "influence-archive-setting" => 1, "influence-disable" => 6, "influence-archive-disable" => 7, "influence-color" => 4, _ => -1 };
        if (expected < 0 || args.Length != expected) throw new InvalidOperationException($"{command}: expected {expected} arguments; see admin --help");
        if (command == "influence") return await rest.Get("influence");
        if (command == "influence-setting") return await rest.Get($"influence/settings/{long.Parse(args[0])}");
        if (command == "influence-archive-setting") return await rest.Get($"influence/archive/{long.Parse(args[0])}/setting");
        if (command == "influence-color") return await rest.Post($"influence/alliances/{long.Parse(args[0])}/color", new { worldId = args[1], paletteIndex = int.Parse(args[2]), requestId = args[3] });
        bool archive = command == "influence-archive-disable";
        int offset = archive ? 1 : 0;
        long settlementId = long.Parse(args[offset]);
        string path = archive ? $"influence/archive/{long.Parse(args[0])}/disable" : $"influence/settings/{settlementId}/disable";
        return await rest.Post(path, new
        {
            requestId = args[offset + 1], expectedVersion = long.Parse(args[offset + 5]),
            context = new { worldInstanceId = args[offset + 2], settlementId, saveGeneration = long.Parse(args[offset + 3]), allianceId = long.Parse(args[offset + 4]) },
        });
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
