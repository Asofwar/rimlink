using System.CommandLine;
using Rimlink.Protocol.ModpackDelivery;

namespace Rimlink.Tools.Commands;

public static class ModpackPackageCommand
{
    public static Command Build()
    {
        var root = new Command("package", "Prepare and publish a server-hosted mod package without Steam");
        var source = new Option<string>("--source") { Required = true, Description = "Separate prepared set of redistributable mod folders; never the game directory" };
        var requirements = new Option<string>("--requirements") { Required = true, Description = "Collection descriptor exported from the server" };
        var output = new Option<string>("--output") { Required = true, Description = "New output directory" };
        var pack = new Command("pack", "Validate the complete prepared collection and create archive.zip + manifest.json") { source, requirements, output };
        pack.SetAction(parse =>
        {
            string file = parse.GetValue(requirements)!;
            if (new FileInfo(file).Length > DeliveryLimits.MaxManifestBytes) throw new DeliveryException("Limit");
            var collection = DeliveryJson.Read<CollectionDescriptor>(File.ReadAllBytes(file));
            Console.WriteLine("Required collection:");
            foreach (var entry in collection.Entries) Console.WriteLine($"{entry.Position}. {entry.PackageId} {entry.Version} ({entry.Kind})");
            var prepared = SafeModArchive.PackPreparedCollection(parse.GetValue(source)!, collection, parse.GetValue(output)!);
            Console.WriteLine($"{prepared.Manifest.Mods.Length} mods; {prepared.Manifest.ArchiveBytes} archive bytes; {prepared.Manifest.ExpandedBytes} expanded bytes; SHA256 {prepared.Manifest.ArchiveSha256}");
        });
        root.Subcommands.Add(pack);

        foreach (string action in new[] { "requirements", "publish", "status", "unpublish" })
        {
            var server = new Option<string>("--server") { Required = true };
            var token = new Option<string>("--token") { Required = true };
            var insecure = new Option<bool>("--insecure") { Description = "Accept a self-signed server certificate" };
            var from = new Option<string>("--from") { Required = action == "publish" };
            var destination = new Option<string>("--output") { Required = action == "requirements" };
            var redistribution = new Option<bool>("--confirm-redistribution");
            var publicAccess = new Option<bool>("--confirm-public");
            var sharedSettings = new Option<bool>("--confirm-shared-settings");
            var command = new Command(action, "Server package " + action) { server, token, insecure };
            if (action == "requirements") command.Options.Add(destination);
            if (action == "publish") { command.Options.Add(from); command.Options.Add(redistribution); command.Options.Add(publicAccess); command.Options.Add(sharedSettings); }
            command.SetAction(async parse =>
            {
                if (action == "publish" && !parse.GetValue(redistribution)) throw new InvalidOperationException("--confirm-redistribution is required");
                using var rest = new RestClient(parse.GetValue(server)!, parse.GetValue(token)!, parse.GetValue(insecure), DeliveryLimits.AccessLifetime);
                const string prefix = "modpack/delivery";
                if (action == "requirements")
                {
                    string json = await rest.Get(prefix + "/requirements");
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    _ = DeliveryJson.Read<CollectionDescriptor>(bytes);
                    await using var file = new FileStream(parse.GetValue(destination)!, FileMode.CreateNew, FileAccess.Write);
                    await file.WriteAsync(bytes);
                }
                else if (action == "publish") Console.WriteLine(await Publish(rest, parse.GetValue(from)!, parse.GetValue(publicAccess), parse.GetValue(sharedSettings)));
                else Console.WriteLine(action == "status" ? await rest.Get(prefix + "/publication") : await rest.Delete(prefix + "/publication"));
            });
            root.Subcommands.Add(command);
        }
        return root;
    }

    public static async Task<string> Publish(RestClient rest, string directory, bool publicAccess, bool sharedSettings)
    {
        string manifestPath = Path.Combine(directory, "manifest.json"), archivePath = Path.Combine(directory, "archive.zip");
        if (new FileInfo(manifestPath).Length > DeliveryLimits.MaxManifestBytes) throw new DeliveryException("Limit");
        var manifest = DeliveryJson.Read<PackageManifest>(await File.ReadAllBytesAsync(manifestPath));
        SafeModArchive.ValidateArchive(archivePath, manifest);
        var accepted = DeliveryJson.Read<UploadAccepted>(System.Text.Encoding.UTF8.GetBytes(await rest.Post("modpack/delivery/packages", new UploadOffer(manifest, true, publicAccess))));
        await rest.Upload("modpack/delivery/packages/" + accepted.PackageId + "/archive", archivePath);
        return await rest.Post("modpack/delivery/packages/" + accepted.PackageId + "/publish", new PublishPackage(manifest.CollectionRevision, manifest.CatalogHash, sharedSettings));
    }
}
