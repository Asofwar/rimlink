using System.Net;
using System.Net.Http.Headers;
using Rimlink.Client.Mods;
using Rimlink.Client.Mods.Delivery;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Mods;

public sealed class ModpackDeliveryFlowTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task CliPackageDownloadsResumesInstallsAndAnOrdinaryPlayerLogsIn(bool serverIgnoresRange, bool optionalSettings, bool changedTexture)
    {
        using var fixture = await ModpackDeliveryFixture.Create(optionalSettings: optionalSettings);
        string profile = Path.Combine(fixture.Server.DataDir, "profile"), mods = Path.Combine(fixture.Server.DataDir, "game", "Mods"), core = Path.Combine(fixture.Server.DataDir, "game", "Data", "Core");
        Directory.CreateDirectory(core); Directory.CreateDirectory(mods); Directory.CreateDirectory(profile);
        var endpoint = new ServerEndpoint("fixture", 25560);
        var handler = new ObservedHandler(fixture.Server.Server.CreateHandler(), serverIgnoresRange);
        using var client = new ModpackDeliveryClient(endpoint, new CertificatePinStore(Path.Combine(profile, "pins.json")), new DeliveryDownloadTests.RefusePin(), new(fixture.Server.Server.BaseAddress, handler));
        var capabilities = await client.Capabilities(CancellationToken.None); Assert.True(capabilities.PasswordLoginAvailable);
        var grant = await client.Access("test-password", CancellationToken.None);
        var (manifest, settings) = await client.Preview(CancellationToken.None);
        Assert.Contains(settings.Configs, c => c.Handle == "NewSettingsClass");
        string cache = Path.Combine(profile, "cache"); Directory.CreateDirectory(cache);
        string partial = Path.Combine(cache, manifest.PackageId + ".part");
        byte[] archive = await File.ReadAllBytesAsync(fixture.Archive); await File.WriteAllBytesAsync(partial, archive[..(archive.Length / 2)]);
        await File.WriteAllBytesAsync(partial + ".json", DeliveryJson.Write(new { server = endpoint.Id, fingerprint = (string?)null, manifest.PackageId, manifest.CatalogHash, manifest.ArchiveSha256, manifest.ArchiveBytes }));
        var downloaded = await client.Download(manifest, settings, cache, null, CancellationToken.None);
        Assert.NotNull(handler.Range);
        Assert.Equal(archive, await File.ReadAllBytesAsync(downloaded.ArchivePath));
        Assert.DoesNotContain(grant.Token, await File.ReadAllTextAsync(partial + ".json"));
        Assert.DoesNotContain("test-password", await File.ReadAllTextAsync(partial + ".json"));
        string config = Path.Combine(profile, "Config", "ModsConfig.xml"), state = Path.Combine(profile, "Rimlink");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!); await File.WriteAllTextAsync(config, "<ModsConfigData><version>1.7</version><activeMods><li>ludeon.rimworld</li></activeMods></ModsConfigData>");
        string outbox = Path.Combine(profile, "outbox.json"); await File.WriteAllTextAsync(outbox, "keep-outbox");
        using var installer = new ModpackInstaller(mods, config, state);
        var installedCore = new InstalledDeliveryMod("ludeon.rimworld", "ludeon.rimworld", core, "", new("", false, false, false));
        var plan = installer.Plan(downloaded, [installedCore], ["ludeon.rimworld"]);
        installer.Commit(plan, downloaded, "fixture_25560", "Fixture", new("fixture", 25560, "Fixture", "nova", DateTime.UtcNow), [], CancellationToken.None);
        Assert.True(installer.IsPendingForServer(endpoint.Id)); Assert.False(installer.IsPendingForServer("another_25560"));
        Assert.Equal("keep-outbox", await File.ReadAllTextAsync(outbox));
        Assert.NotNull(new JoinIntentStore(Path.Combine(state, "join-intent.json")).Take(DateTime.UtcNow));
        Assert.Null(new JoinIntentStore(Path.Combine(state, "join-intent.json")).Take(DateTime.UtcNow));
        var loaded = plan.Future.Select((m, i) => (m with { Content = DeliveryValidation.Official(m.PackageId) ? m.Content : ModContent.Scan(m.Root) }).Reference(i)).ToList();
        var sharedHashes = settings.Configs.Where(c => plan.Future.Any(m => m.PackageId == c.PackageId)).Select(c => new ModSettingsHash { PackageId = c.PackageId, Handle = c.Handle, Hash = c.Hash }).ToList();
        var roots = plan.Future.ToDictionary(m => m.PackageId, m => m.Root);
        Assert.False(installer.VerifyAfterRestart(loaded, sharedHashes, roots, true)); // The same process cannot prove a restart.
        installer.LaunchId = Guid.NewGuid().ToString("N");
        Assert.False(installer.VerifyAfterRestart(loaded, sharedHashes, roots, false));
        Assert.False(installer.VerifyAfterRestart(loaded, [], roots, true)); // A previously unknown handle is mandatory, not filtered out.
        using var channel = fixture.Server.CreateChannel(); var auth = new Auth.AuthClient(channel);
        var mismatch = await auth.RegisterAsync(new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "Nova" });
        Assert.Empty(mismatch.SessionToken);
        var request = new LoginRequest { ProtocolVersion = ProtocolVersion.Current, Password = new() { Login = "nova", Password = "pw" }, ServerPassword = "test-password" }; request.Mods.AddRange(loaded); request.ModSettings.AddRange(sharedHashes);
        var login = await auth.LoginAsync(request); Assert.NotEmpty(login.SessionToken);
        if (changedTexture)
        {
            string texture = Path.Combine(roots["test.demo"], "Textures", "changed.txt"); Directory.CreateDirectory(Path.GetDirectoryName(texture)!); await File.WriteAllTextAsync(texture, "changed after install");
            bool verified = installer.VerifyAfterRestart(loaded, sharedHashes, roots, true);
            Assert.False(verified); Assert.False(ModpackJoinReadiness.MayContinue(verified, Role.Player, true)); Assert.NotNull(installer.State);
            File.Delete(texture); Directory.Delete(Path.GetDirectoryName(texture)!);
        }
        if (optionalSettings)
        {
            var store = new ServerSettingsStore(Path.Combine(state, "server-settings")); var set = store.ReadActive()!;
            string optional = store.PathFor(set.Server, new("test.optional", "OptionalClass")); byte[] original = await File.ReadAllBytesAsync(optional);
            await File.WriteAllTextAsync(optional, "<Changed/>"); Assert.False(installer.VerifyAfterRestart(loaded, sharedHashes, roots, true));
            await File.WriteAllBytesAsync(optional, original);
        }
        Assert.True(installer.VerifyAfterRestart(loaded, sharedHashes, roots, true)); Assert.Null(installer.State);
        Assert.Equal("keep-outbox", await File.ReadAllTextAsync(outbox));
    }
    private sealed class ObservedHandler(HttpMessageHandler inner, bool ignoreRange) : DelegatingHandler(inner)
    {
        public RangeHeaderValue? Range { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/archive", StringComparison.Ordinal)) { Range = request.Headers.Range; if (ignoreRange) request.Headers.Range = null; }
            return base.SendAsync(request, ct);
        }
    }
}
