using System.CommandLine;
using System.Xml.Linq;

namespace Rimlink.Tools.Commands;

/// <summary>Reads the active mod list of a RimWorld install and uploads it as the server modpack.</summary>
public static class ModpackCommand
{
    public sealed record Entry(int Position, string PackageId, string Version, ulong WorkshopId, string Url, string Kind);

    public static Command Build()
    {
        var modsConfig = new Option<string>("--from") { Description = "Path to ModsConfig.xml", Required = true };
        var modsDirs = new Option<string[]>("--mods-dir") { Description = "Mod folders to scan for About.xml (game Mods folder, workshop content folder)", AllowMultipleArgumentsPerToken = true };
        var server = new Option<string>("--server") { Description = "Server base URL, e.g. https://host:25560" };
        var token = new Option<string>("--token") { Description = "Admin:ApiToken" };
        var insecure = new Option<bool>("--insecure") { Description = "Accept a self-signed server certificate" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the list instead of uploading" };

        var import = new Command("import", "Build the modpack from ModsConfig.xml + About.xml files and upload it") { modsConfig, modsDirs, server, token, insecure, dryRun };
        import.SetAction(async parse =>
        {
            var entries = Collect(parse.GetValue(modsConfig)!, parse.GetValue(modsDirs) ?? []);
            if (parse.GetValue(dryRun) || parse.GetValue(server) is null)
            {
                foreach (var e in entries) Console.WriteLine($"{e.Position}. {e.PackageId} {e.Version} {(e.WorkshopId != 0 ? $"workshop:{e.WorkshopId}" : "")}");
                return;
            }
            using var rest = new RestClient(parse.GetValue(server)!, parse.GetValue(token) ?? throw new InvalidOperationException("--token is required"), parse.GetValue(insecure));
            Console.WriteLine(await rest.Put("modpack", new { entries }));
        });

        return new Command("modpack", "Server modpack management") { import, ModpackDefsCommand.Build() };
    }

    public static List<Entry> Collect(string modsConfigPath, string[] modsDirs)
    {
        var active = XDocument.Load(modsConfigPath).Root!.Element("activeMods")!.Elements("li").Select(e => e.Value.Trim().ToLowerInvariant()).ToList();

        var known = new Dictionary<string, (string version, ulong workshop)>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in modsDirs.Where(Directory.Exists))
        {
            foreach (string about in Directory.EnumerateFiles(dir, "About.xml", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(Path.GetDirectoryName(about)!).Equals("About", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var meta = XDocument.Load(about).Root!;
                    string? id = meta.Element("packageId")?.Value.Trim().ToLowerInvariant();
                    if (id is null) continue;
                    string version = meta.Element("modVersion")?.Value.Trim() ?? "";
                    string modRoot = Path.GetDirectoryName(Path.GetDirectoryName(about)!)!;
                    string idFile = Path.Combine(modRoot, "About", "PublishedFileId.txt");
                    ulong workshop = File.Exists(idFile) && ulong.TryParse(File.ReadAllText(idFile).Trim(), out var w) ? w : 0;
                    known[id] = (version, workshop);
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
                {
                    Console.Error.WriteLine($"skipping {about}: {ex.Message}");
                }
            }
        }

        return active.Select((id, i) =>
        {
            string bare = id.EndsWith("_steam") ? id[..^"_steam".Length] : id;
            known.TryGetValue(bare, out var info);
            return new Entry(i, bare, info.version ?? "", info.workshop, "", "required");
        }).ToList();
    }
}
