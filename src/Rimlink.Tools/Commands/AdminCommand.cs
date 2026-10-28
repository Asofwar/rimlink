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
        var name = new Argument<string>("command") { Description = "online | kick <playerId> [reason] | ban <playerId> [days] [reason] | pardon <banId> | bans | mute <playerId> <minutes> | unmute <playerId> | saves <playerId> | rollback <playerId> <version> | reset <playerId> | broadcast <text> | shutdown <seconds> [reason] | recreateworld | modpack | actions" };
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
                var other => throw new InvalidOperationException($"unknown command '{other}'"),
            };
            Console.WriteLine(result);
        });
        return command;
    }
}
