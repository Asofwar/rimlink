extern alias tools;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;
using Role = Rimlink.Server.Storage.Entities.Role;
using tools::Rimlink.Tools.Commands;

namespace Rimlink.Server.Tests.Support;

/// <summary>Only test-owned mod content: a complete package with a newly introduced shared-settings handle.</summary>
public sealed class ModpackDeliveryFixture : IDisposable
{
    public ServerFixture Server { get; } = new ServerFixture().WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken).WithSetting("Auth:AllowPassword", "true").WithSetting("Server:ServerPassword", "test-password");
    public HttpClient Admin { get; private set; } = null!;
    public PackageManifest Manifest { get; private set; } = null!;
    public string PackageDirectory => Path.Combine(Server.DataDir, "packed");
    public string Archive => Path.Combine(PackageDirectory, "archive.zip");

    public static async Task<ModpackDeliveryFixture> Create(bool publish = true, bool large = false, bool optionalSettings = false)
    {
        var fixture = new ModpackDeliveryFixture();
        try
        {
            fixture.Admin = fixture.Server.Rest();
            var entries = new List<ModpackEntry> { new() { PackageId = "ludeon.rimworld" } };
            int position = 1;
            foreach (string id in new[] { "asofwar.rimlink", "test.demo" })
            {
                string root = Path.Combine(fixture.Server.DataDir, "prepared", id);
                Directory.CreateDirectory(Path.Combine(root, "About")); Directory.CreateDirectory(Path.Combine(root, "Defs"));
                File.WriteAllText(Path.Combine(root, "About", "About.xml"), $"<ModMetaData><packageId>{id}</packageId><name>Test owned {id}</name><modVersion>1</modVersion><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
                File.WriteAllText(Path.Combine(root, "Defs", "a.xml"), "<Defs/>");
                entries.Add(new() { Position = position++, PackageId = id, Version = "1", FilesHash = ModContent.Scan(root).FilesHash });
            }
            if (large)
            {
                string textures = Path.Combine(fixture.Server.DataDir, "prepared", "test.demo", "Textures"); Directory.CreateDirectory(textures);
                byte[] payload = new byte[1024 * 1024]; new Random(17).NextBytes(payload); File.WriteAllBytes(Path.Combine(textures, "fixture.bin"), payload);
            }
            using (var scope = fixture.Server.Services.CreateScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<ModpackService>();
                if (optionalSettings) entries.Add(new ModpackEntry { PackageId = "test.optional", Position = position, Kind = EntryKind.Allowed });
                await service.Set(entries, Role.Admin, "test");
                var configs = new List<ModConfig> { new() { PackageId = "test.demo", Handle = "NewSettingsClass", Content = Google.Protobuf.ByteString.CopyFromUtf8("<TestSettings><Enabled>True</Enabled></TestSettings>") } };
                if (optionalSettings) configs.Add(new() { PackageId = "test.optional", Handle = "OptionalClass", Content = Google.Protobuf.ByteString.CopyFromUtf8("<OptionalSettings/>") });
                await service.SetConfigs(configs, Role.Admin, "test");
            }
            string requirements = await fixture.Admin.GetStringAsync("/api/admin/modpack/delivery/requirements");
            string path = Path.Combine(fixture.Server.DataDir, "requirements.json"); await File.WriteAllTextAsync(path, requirements);
            int result = await ModpackPackageCommand.Build().Parse(["pack", "--source", Path.Combine(fixture.Server.DataDir, "prepared"), "--requirements", path, "--output", fixture.PackageDirectory]).InvokeAsync();
            if (result != 0) throw new InvalidOperationException("test package command failed");
            fixture.Manifest = DeliveryJson.Read<PackageManifest>(await File.ReadAllBytesAsync(Path.Combine(fixture.PackageDirectory, "manifest.json")));
            if (publish)
            {
                await fixture.Publish();
            }
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    public async Task Publish()
    {
        using var http = Server.Rest(); http.BaseAddress = new Uri(http.BaseAddress!, "api/admin/");
        using var cli = new RestClient(http);
        await ModpackPackageCommand.Publish(cli, PackageDirectory, publicAccess: false, sharedSettings: true);
    }
    public void Dispose() { Admin?.Dispose(); Server.Dispose(); }
}
