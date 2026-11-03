using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Modpack;

public sealed class ModpackDeliveryTests
{
    private const string Admin = "/api/admin/modpack/delivery", Public = "/api/modpack-delivery";

    [Fact]
    public async Task CapabilitiesDiscloseNoPackageAndAllAdminRoutesRequireAScope()
    {
        using var server = new ServerFixture().WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken).WithToken(0, "reader", "delivery-read-token-for-tests", "read");
        using var anonymous = server.CreateClient();
        var capabilities = await anonymous.GetFromJsonAsync<DeliveryCapabilities>(Public + "/capabilities", DeliveryJson.Options);
        Assert.False(capabilities!.DeliveryAvailable);
        Assert.DoesNotContain("packageId", await anonymous.GetStringAsync(Public + "/capabilities"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Admin + "/requirements")).StatusCode);
        using var reader = server.Rest("delivery-read-token-for-tests");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Admin + "/packages", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync(Admin + "/publication")).StatusCode);
    }

    [Fact]
    public async Task PreparedBytesPublishDownloadByRangeAndInvalidateWhenRequirementsChange()
    {
        using var server = new ServerFixture().WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken).WithSetting("Server:ServerPassword", "test-password");
        using var admin = server.Rest();
        string source = Path.Combine(server.DataDir, "prepared");
        string mod = Path.Combine(source, "demo"); Directory.CreateDirectory(Path.Combine(mod, "About"));
        File.WriteAllText(Path.Combine(mod, "About", "About.xml"), "<ModMetaData><packageId>test.demo</packageId><name>Demo</name><modVersion>1</modVersion><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
        Directory.CreateDirectory(Path.Combine(mod, "Defs")); File.WriteAllText(Path.Combine(mod, "Defs", "a.xml"), "<Defs/>");
        using (var scope = server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ModpackService>().Set([new ModpackEntry { PackageId = "ludeon.rimworld" }, new ModpackEntry { PackageId = "test.demo", Position = 1, Version = "1", FilesHash = ModContent.Scan(mod).FilesHash }], Server.Storage.Entities.Role.Admin, "test");
        var descriptor = (await admin.GetFromJsonAsync<CollectionDescriptor>(Admin + "/requirements", DeliveryJson.Options))!;
        var packed = SafeModArchive.PackPreparedCollection(source, descriptor, Path.Combine(server.DataDir, "packed"));
        using var refused = await admin.PostAsJsonAsync(Admin + "/packages", new UploadOffer(packed.Manifest, false, false), DeliveryJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var offer = await admin.PostAsJsonAsync(Admin + "/packages", new UploadOffer(packed.Manifest, true, false), DeliveryJson.Options);
        offer.EnsureSuccessStatusCode();
        var accepted = (await offer.Content.ReadFromJsonAsync<UploadAccepted>(DeliveryJson.Options))!;
        using var zip = new ByteArrayContent(await File.ReadAllBytesAsync(packed.ArchivePath));
        zip.Headers.ContentType = new("application/zip");
        (await admin.PutAsync(Admin + "/packages/" + accepted.PackageId + "/archive", zip)).EnsureSuccessStatusCode();
        var publish = new PublishPackage(descriptor.Revision, descriptor.CatalogHash, false);
        (await admin.PostAsJsonAsync(Admin + "/packages/" + accepted.PackageId + "/publish", publish, DeliveryJson.Options)).EnsureSuccessStatusCode();
        // Publishing the same verified package is idempotent.
        (await admin.PostAsJsonAsync(Admin + "/packages/" + accepted.PackageId + "/publish", publish, DeliveryJson.Options)).EnsureSuccessStatusCode();
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Public + "/access", new AccessRequest("wrong"), DeliveryJson.Options)).StatusCode);
        using var access = await client.PostAsJsonAsync(Public + "/access", new AccessRequest("test-password"), DeliveryJson.Options);
        access.EnsureSuccessStatusCode();
        var grant = (await access.Content.ReadFromJsonAsync<AccessGrant>(DeliveryJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.Token);
        string url = Public + "/packages/" + accepted.PackageId;
        var manifest = (await client.GetFromJsonAsync<PackageManifest>(url + "/manifest", DeliveryJson.Options))!;
        Assert.Equal(accepted.PackageId, manifest.PackageId);
        using var range = new HttpRequestMessage(HttpMethod.Get, url + "/archive"); range.Headers.Range = new RangeHeaderValue(5, 19);
        using var bytes = await client.SendAsync(range); Assert.Equal(HttpStatusCode.PartialContent, bytes.StatusCode);
        Assert.Equal((await File.ReadAllBytesAsync(packed.ArchivePath))[5..20], await bytes.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"" + manifest.ArchiveSha256 + "\"", bytes.Headers.ETag!.Tag);
        using (var scope = server.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<ModpackService>().Set([new ModpackEntry { PackageId = "ludeon.rimworld" }], Server.Storage.Entities.Role.Admin, "test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url + "/manifest")).StatusCode);
        Assert.False((await client.GetFromJsonAsync<DeliveryCapabilities>(Public + "/capabilities", DeliveryJson.Options))!.DeliveryAvailable);
    }

    [Fact]
    public async Task SettingsNeedExplicitConsentAndUnpublishingCancelsExistingGrants()
    {
        using var fixture = await ModpackDeliveryFixture.Create(publish: false);
        var server = fixture.Server; var admin = fixture.Admin;
        using var offer = await admin.PostAsJsonAsync(Admin + "/packages", new UploadOffer(fixture.Manifest, true, false), DeliveryJson.Options); offer.EnsureSuccessStatusCode();
        var accepted = (await offer.Content.ReadFromJsonAsync<UploadAccepted>(DeliveryJson.Options))!;
        using var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(fixture.Archive)); bytes.Headers.ContentType = new("application/zip");
        (await admin.PutAsync(Admin + "/packages/" + accepted.PackageId + "/archive", bytes)).EnsureSuccessStatusCode();
        string publish = Admin + "/packages/" + accepted.PackageId + "/publish";
        var request = new PublishPackage(fixture.Manifest.CollectionRevision, fixture.Manifest.CatalogHash, false);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(publish, request, DeliveryJson.Options)).StatusCode);
        (await admin.PostAsJsonAsync(publish, request with { SharedSettingsConfirmed = true }, DeliveryJson.Options)).EnsureSuccessStatusCode();
        using var client = server.CreateClient();
        using var response = await client.PostAsJsonAsync(Public + "/access", new AccessRequest("test-password"), DeliveryJson.Options); response.EnsureSuccessStatusCode();
        var grant = (await response.Content.ReadFromJsonAsync<AccessGrant>(DeliveryJson.Options))!;
        var runtime = server.Services.GetRequiredService<DeliveryRuntime>(); var existing = runtime.Find(grant.Token, grant.PackageId);
        using var transfer = runtime.Transfer(existing);
        (await admin.DeleteAsync(Admin + "/publication")).EnsureSuccessStatusCode();
        Assert.True(existing.Cancellation.IsCancellationRequested);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Public + "/packages/" + grant.PackageId + "/settings")).StatusCode);
    }

    [Fact]
    public async Task CorruptedPublishedBytesBecomeUnavailableWithoutBreakingTheServer()
    {
        using var fixture = await ModpackDeliveryFixture.Create();
        using var scope = fixture.Server.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ModpackDeliveryService>();
        var publication = await service.Publication(CancellationToken.None); Assert.True(publication.Available);
        string archive = service.ArchivePath(publication.PackageId!);
        using (var file = new FileStream(archive, FileMode.Open, FileAccess.Write, FileShare.None)) file.WriteByte(0xff);
        Assert.False((await service.Publication(CancellationToken.None)).Available);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Server.CreateClient().GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task BootstrapRejectsUnknownFieldsAndBoundsPasswordAttempts()
    {
        using var server = new ServerFixture(); using var client = server.CreateClient();
        using var bad = new StringContent("{\"serverPassword\":\"never-log-me\",\"archiveUrl\":\"file:///secret\"}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(Public + "/access", bad);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("never-log-me", await response.Content.ReadAsStringAsync());
        for (int i = 0; i < 4; i++) await client.PostAsJsonAsync(Public + "/access", new AccessRequest("wrong"), DeliveryJson.Options);
        using var limited = await client.PostAsJsonAsync(Public + "/access", new AccessRequest("wrong"), DeliveryJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task RepeatedPublicationKeepsOnlyCurrentAndPreviousAndReapsRestoredOrphans()
    {
        using var fixture = await ModpackDeliveryFixture.Create();
        for (int i = 0; i < 3; i++) await fixture.Publish();
        using var scope = fixture.Server.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<Server.Storage.RimlinkDbContext>();
        Assert.Equal(2, await db.Set<Server.Storage.Entities.ModpackPackage>().CountAsync());
        string folder = Path.Combine(fixture.Server.DataDir, "modpack-delivery", "packages");
        string orphan = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip"), note = Path.Combine(folder, "operator-note.txt");
        await File.WriteAllTextAsync(orphan, "old restored orphan"); File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1)); await File.WriteAllTextAsync(note, "keep");
        await fixture.Publish();
        Assert.False(File.Exists(orphan)); Assert.Equal("keep", await File.ReadAllTextAsync(note));
        Assert.Equal(2, Directory.GetFiles(folder, "*.zip").Length);
    }

    [Fact]
    public async Task UnpublishingAbortsAnAlreadyStreamingHttpResponse()
    {
        using var fixture = await ModpackDeliveryFixture.Create(large: true); using var client = fixture.Server.CreateClient();
        using var access = await client.PostAsJsonAsync(Public + "/access", new AccessRequest("test-password"), DeliveryJson.Options); access.EnsureSuccessStatusCode();
        var grant = (await access.Content.ReadFromJsonAsync<AccessGrant>(DeliveryJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", grant.Token);
        using var response = await client.GetAsync(Public + "/packages/" + grant.PackageId + "/archive", HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync();
        Assert.Equal(1, await body.ReadAsync(new byte[1]));
        var runtime = fixture.Server.Services.GetRequiredService<DeliveryRuntime>(); Assert.True(runtime.HasTransfers);
        (await fixture.Admin.DeleteAsync(Admin + "/publication")).EnsureSuccessStatusCode();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var error = await Record.ExceptionAsync(() => body.CopyToAsync(Stream.Null, deadline.Token));
        Assert.NotNull(error); Assert.False(deadline.IsCancellationRequested);
    }

    [Fact]
    public async Task DownloadPolicyUsesTheSameAppliedOptionsAsLoginUntilServerRestart()
    {
        var serverPolicy = new MutableMonitor<ServerOptions>(new() { ServerPassword = "original" });
        var authPolicy = new MutableMonitor<AuthOptions>(new() { AllowPassword = false });
        using var server = new ServerFixture().WithSetting("Server:ServerPassword", "original").WithSetting("Auth:AllowPassword", "false")
            .WithServices(services => { services.AddSingleton<IOptionsMonitor<ServerOptions>>(serverPolicy); services.AddSingleton<IOptionsMonitor<AuthOptions>>(authPolicy); });
        using var client = server.CreateClient();
        _ = server.Services.GetRequiredService<IOptions<ServerOptions>>().Value; _ = server.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        serverPolicy.Change(new() { ServerPassword = "" }); authPolicy.Change(new() { AllowPassword = true });
        var capabilities = (await client.GetFromJsonAsync<DeliveryCapabilities>(Public + "/capabilities", DeliveryJson.Options))!;
        Assert.False(capabilities.PasswordLoginAvailable); Assert.True(capabilities.RequiresServerPassword);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync(Public + "/access", new AccessRequest("original"), DeliveryJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Public + "/access", new AccessRequest(""), DeliveryJson.Options)).StatusCode);
        using var restarted = new ServerFixture().WithSetting("Server:ServerPassword", "").WithSetting("Auth:AllowPassword", "true");
        using var nextClient = restarted.CreateClient(); var next = (await nextClient.GetFromJsonAsync<DeliveryCapabilities>(Public + "/capabilities", DeliveryJson.Options))!;
        Assert.True(next.PasswordLoginAvailable); Assert.False(next.RequiresServerPassword);
    }
    private sealed class MutableMonitor<T>(T initial) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue { get; private set; } = initial;
        private event Action<T, string?>? Changed;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) { Changed += listener; return new Subscription(() => Changed -= listener); }
        public void Change(T value) { CurrentValue = value; Changed?.Invoke(value, Options.DefaultName); }
        private sealed class Subscription(Action remove) : IDisposable { public void Dispose() => remove(); }
    }
}
