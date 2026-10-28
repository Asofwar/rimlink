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
        var name = new Argument<string>("command") { Description = "online | kick <playerId> [reason] | ban <playerId> [days] [reason] | pardon <banId> | bans | mute <playerId> <minutes> | unmute <playerId> | saves <playerId> | rollback <playerId> <version> | reset <playerId> | broadcast <text> | shutdown <seconds> [reason] | recreateworld | modpack | actions | wallet <playerId> [+n|-n] [reason] | parcels <playerId> | lots [playerId] | refund lot <id> | refund parcel <id> | economy freeze [reason]|unfreeze|stats | raids list [playerId]|cancel <id> [reason]|freeze [reason]|unfreeze|stats | peaceful <playerId> on|off | shop <playerId> | removals <playerId>" };
        var args = new Argument<string[]>("args") { Arity = ArgumentArity.ZeroOrMore };

        var command = new Command("admin", "Run an admin operation on a running server") { server, token, insecure, name, args };
        command.SetAction(async parse =>
        {
            using var rest = new RestClient(parse.GetValue(server)!, parse.GetValue(token)!, parse.GetValue(insecure));
            string[] a = parse.GetValue(args) ?? [];
            string rest1 = string.Join(' ', a.Skip(1));
            string result = parse.GetValue(name)! switch
            {
                "online" => await rest.Get("players/online"),
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
                "shutdown" => await rest.Post("shutdown", new { inSeconds = int.Parse(a[0]), reason = rest1 }),
                "recreateworld" => await rest.Post("world/recreate", null),
                "modpack" => await rest.Get("modpack"),
                "actions" => await rest.Get("actions"),
                "wallet" => await rest.Post($"economy/wallet/{a[0]}", new { delta = long.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), reason = string.Join(' ', a.Skip(2)) }),
                "parcels" => await rest.Get($"economy/parcels/{a[0]}"),
                "lots" => await rest.Get(a.Length > 0 ? $"economy/lots?playerId={a[0]}" : "economy/lots"),
                "refund" => await rest.Post($"economy/refund/{a[0]}/{a[1]}", null),
                "raids" => a[0] switch
                {
                    "list" => await rest.Get(a.Length > 1 ? $"raids?playerId={a[1]}" : "raids"),
                    "cancel" => await rest.Post($"raids/{a[1]}/cancel", new { reason = string.Join(' ', a.Skip(2).Where(x => x != "--return")), returnPawns = a.Contains("--return") }),
                    "freeze" => await rest.Post("raids/freeze", new { frozen = true, reason = rest1 }),
                    "unfreeze" => await rest.Post("raids/freeze", new { frozen = false, reason = (string?)null }),
                    "stats" => await rest.Get("raids/stats"),
                    _ => throw new InvalidOperationException("raids list [playerId] | cancel <id> [reason] | freeze [reason] | unfreeze | stats"),
                },
                "shop" => await rest.Get($"shop/{a[0]}"),
                "removals" => await rest.Get($"shop/{a[0]}/removals"),
                "peaceful" => await rest.Post($"players/{a[0]}/peaceful", new { peaceful = a[1] == "on" }),
                "economy" => a[0] switch
                {
                    "freeze" => await rest.Post("economy/freeze", new { frozen = true, reason = rest1 }),
                    "unfreeze" => await rest.Post("economy/freeze", new { frozen = false, reason = (string?)null }),
                    "stats" => await rest.Get("economy/stats"),
                    _ => throw new InvalidOperationException("economy freeze [reason] | unfreeze | stats"),
                },
                var other => throw new InvalidOperationException($"unknown command '{other}'"),
            };
            Console.WriteLine(result);
        });
        return command;
    }
}
