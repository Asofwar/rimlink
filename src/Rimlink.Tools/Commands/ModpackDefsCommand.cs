using System.CommandLine;
using Rimlink.Server.Application.Modpack;

namespace Rimlink.Tools.Commands;

/// <summary>`rimlink-tools modpack defs --mods-dir ... [--server URL --token T]`: scans Defs XML and uploads the def-name list.</summary>
public static class ModpackDefsCommand
{
    public static Command Build()
    {
        var modsDirs = new Option<string[]>("--mods-dir") { Description = "Folders to scan recursively for Defs/*.xml (game Data folder, Mods folder, workshop content folder)", Required = true, AllowMultipleArgumentsPerToken = true };
        var server = new Option<string>("--server") { Description = "Server base URL, e.g. https://host:25560" };
        var token = new Option<string>("--token") { Description = "Admin:ApiToken" };
        var insecure = new Option<bool>("--insecure") { Description = "Accept a self-signed server certificate" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the count instead of uploading" };

        var defs = new Command("defs", "Collect ThingDef/PawnKindDef names of the modpack and upload them (empty list = check off)") { modsDirs, server, token, insecure, dryRun };
        defs.SetAction(async parse =>
        {
            var names = DefScanner.Scan(parse.GetValue(modsDirs) ?? []);
            Console.WriteLine($"{names.Count} defs found");
            if (parse.GetValue(dryRun) || parse.GetValue(server) is null)
            {
                foreach (string name in names.Take(20)) Console.WriteLine("  " + name);
                if (names.Count > 20) Console.WriteLine("  ...");
                return;
            }
            using var rest = new RestClient(parse.GetValue(server)!, parse.GetValue(token) ?? throw new InvalidOperationException("--token is required"), parse.GetValue(insecure));
            Console.WriteLine(await rest.Put("modpack/defs", new { defNames = names.ToArray() }));
        });
        return defs;
    }
}
