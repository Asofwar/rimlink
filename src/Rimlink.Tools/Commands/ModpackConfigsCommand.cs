using System.CommandLine;
using System.Xml.Linq;

namespace Rimlink.Tools.Commands;

/// <summary>
/// `modpack configs import|list`: the mods whose settings every player uses. The admin's own settings files are read from the
/// game's Config folder (`Mod_&lt;folder&gt;_&lt;class&gt;.xml`) and uploaded as they are.
/// </summary>
public static class ModpackConfigsCommand
{
    public sealed record ConfigFile(string PackageId, string Handle, string Path, byte[] Content);

    public static Command Build()
    {
        var configDir = new Option<string>("--config-dir") { Description = "The game's Config folder (holds ModsConfig.xml and Mod_*.xml)" };
        var modsDirs = new Option<string[]>("--mods-dir") { Description = "Mod folders to scan for About.xml (game Mods folder, workshop content folder)", AllowMultipleArgumentsPerToken = true };
        var mods = new Option<string[]>("--mod") { Description = "packageId of a mod whose settings every player uses", AllowMultipleArgumentsPerToken = true };
        var clear = new Option<bool>("--clear") { Description = "Stop sharing mod settings" };
        var server = new Option<string>("--server") { Description = "Server base URL, e.g. https://host:25560" };
        var token = new Option<string>("--token") { Description = "Admin:ApiToken" };
        var insecure = new Option<bool>("--insecure") { Description = "Accept a self-signed server certificate" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the files instead of uploading" };

        var import = new Command("import", "Upload the settings files of the given mods (they must load below Rimlink in the modpack)") { configDir, modsDirs, mods, clear, server, token, insecure, dryRun };
        import.SetAction(async parse =>
        {
            var files = parse.GetValue(clear) ? [] : Collect(parse.GetValue(configDir) ?? throw new InvalidOperationException("--config-dir is required"),
                parse.GetValue(modsDirs) ?? [], parse.GetValue(mods) ?? throw new InvalidOperationException("--mod is required"));
            foreach (var f in files) Console.WriteLine($"{f.PackageId} {f.Handle} {f.Content.Length} bytes ({f.Path})");
            if (files.Count > 0)
                Console.WriteLine("Mod settings may hold secrets (Twitch tokens and the like): check these files before sharing them with every player.");
            if (parse.GetValue(dryRun) || parse.GetValue(server) is null) return;
            using var rest = new RestClient(parse.GetValue(server)!, parse.GetValue(token) ?? throw new InvalidOperationException("--token is required"), parse.GetValue(insecure));
            var configs = files.Select(f => new { packageId = f.PackageId, handle = f.Handle, contentBase64 = Convert.ToBase64String(f.Content) }).ToArray();
            Console.WriteLine(await rest.Put("modpack/configs", new { configs }));
        });

        var listServer = new Option<string>("--server") { Description = "Server base URL", Required = true };
        var listToken = new Option<string>("--token") { Description = "Admin:ApiToken", Required = true };
        var listInsecure = new Option<bool>("--insecure") { Description = "Accept a self-signed server certificate" };
        var list = new Command("list", "Show which mod settings the server shares") { listServer, listToken, listInsecure };
        list.SetAction(async parse =>
        {
            using var rest = new RestClient(parse.GetValue(listServer)!, parse.GetValue(listToken)!, parse.GetValue(listInsecure));
            Console.WriteLine(await rest.Get("modpack/configs"));
        });

        return new Command("configs", "Mod settings every player uses (whitelist)") { import, list };
    }

    /// <summary>All settings files of the given mods; a mod that is not installed or has no settings file is an error.</summary>
    public static List<ConfigFile> Collect(string configDir, IReadOnlyList<string> modsDirs, IReadOnlyList<string> packageIds)
    {
        var folders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string dir in modsDirs.Where(Directory.Exists))
        {
            foreach (string about in Directory.EnumerateFiles(dir, "About.xml", SearchOption.AllDirectories))
            {
                string aboutDir = Path.GetDirectoryName(about)!;
                if (!Path.GetFileName(aboutDir).Equals("About", StringComparison.OrdinalIgnoreCase)) continue;
                string? id;
                try { id = XDocument.Load(about).Root!.Element("packageId")?.Value; }
                catch (System.Xml.XmlException) { continue; }
                if (id is null) continue;
                string key = Normalize(id);
                if (!folders.TryGetValue(key, out var names)) folders[key] = names = [];
                names.Add(Path.GetFileName(Path.GetDirectoryName(aboutDir)!));
            }
        }

        var result = new List<ConfigFile>();
        foreach (string raw in packageIds)
        {
            string id = Normalize(raw);
            if (!folders.TryGetValue(id, out var names)) throw new InvalidOperationException($"{id}: no mod with this packageId in the --mods-dir folders");
            var found = new List<ConfigFile>();
            foreach (string folder in names.Distinct())
            {
                string prefix = $"Mod_{folder}_";
                foreach (string path in Directory.EnumerateFiles(configDir, prefix + "*.xml"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    found.Add(new ConfigFile(id, name[prefix.Length..], path, File.ReadAllBytes(path)));
                }
            }
            if (found.Count == 0) throw new InvalidOperationException($"{id}: no settings file (Mod_<folder>_*.xml) in {configDir}; open the mod's settings once in the game");
            result.AddRange(found);
        }
        return result;
    }

    private static string Normalize(string packageId)
    {
        string id = packageId.Trim().ToLowerInvariant();
        return id.EndsWith("_steam", StringComparison.Ordinal) ? id[..^"_steam".Length] : id;
    }
}
