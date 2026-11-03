using System.Net;
using System.Net.Http.Headers;
using Rimlink.Client.Net;
using Rimlink.Protocol.ModpackDelivery;

namespace Rimlink.Client.Mods.Delivery;

public sealed record DeliveryProgress(string Phase, long Bytes, long Total);
public sealed record DownloadedModpack(PackageManifest Manifest, SettingsSnapshot Settings, string ArchivePath);

/// <summary>Fixed-origin pinned HTTPS, bounded JSON and resumable streaming; download grants stay in memory.</summary>
public sealed class ModpackDeliveryClient : IDisposable
{
    private readonly HttpClient http;
    private readonly ServerEndpoint endpoint;
    private readonly CertificatePinStore pins;
    private readonly IPinPrompt prompt;
    private PinCheck? unknown;
    private bool probed;
    private AccessGrant? grant;
    private const string Prefix = "api/modpack-delivery/";
    public ModpackDeliveryClient(ServerEndpoint endpoint, CertificatePinStore pins, IPinPrompt prompt, ConnectionTransport? transport = null)
    {
        this.endpoint = endpoint; this.pins = pins; this.prompt = prompt;
        var connection = transport ?? ConnectionTransport.Pinned(endpoint, pins, check => unknown = check);
        if (transport is null && connection.Address.Scheme != "https") throw new DeliveryException("TrustFirst");
        http = new HttpClient(connection.Handler) { BaseAddress = connection.Address, Timeout = Timeout.InfiniteTimeSpan, DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
    }
    public async Task<DeliveryCapabilities> Capabilities(CancellationToken ct)
    {
        DeliveryCapabilities capabilities;
        try { capabilities = await Get<DeliveryCapabilities>(Prefix + "capabilities", DeliveryLimits.SimpleBodyBytes, null, ct); }
        catch (HttpRequestException) when (unknown is { } check)
        {
            if (!await prompt.Confirm(endpoint, check.Fingerprint, check.Previous).WaitAsync(ct)) throw new DeliveryException("TrustRefused");
            pins.Save(endpoint, check.Fingerprint); unknown = null;
            capabilities = await Get<DeliveryCapabilities>(Prefix + "capabilities", DeliveryLimits.SimpleBodyBytes, null, ct);
        }
        if (capabilities.FormatVersion != DeliveryLimits.FormatVersion) throw new DeliveryException("Unavailable");
        probed = true; return capabilities;
    }
    public async Task<AccessGrant> Access(string? serverPassword, CancellationToken ct)
    {
        if (!probed) throw new DeliveryException("TrustFirst");
        using var request = new HttpRequestMessage(HttpMethod.Post, Prefix + "access") { Content = new ByteArrayContent(DeliveryJson.Write(new AccessRequest(serverPassword))) };
        request.Content.Headers.ContentType = new("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await Check(response, ct);
        var result = DeliveryJson.Read<AccessGrant>(await Read(response.Content, DeliveryLimits.SimpleBodyBytes, ct), DeliveryLimits.SimpleBodyBytes);
        if (!DeliveryValidation.Id(result.PackageId) || !DeliveryValidation.Sha256(result.CollectionRevision) || !DeliveryValidation.Sha256(result.CatalogHash) || result.Token is not { Length: 64 }
            || !result.Token.All(char.IsAsciiHexDigit) || result.ExpiresAt <= DateTime.UtcNow || result.ExpiresAt > DateTime.UtcNow + DeliveryLimits.AccessLifetime + TimeSpan.FromMinutes(1)) throw new DeliveryException("InvalidManifest");
        grant = result; return result;
    }
    public async Task<(PackageManifest Manifest, SettingsSnapshot Settings)> Preview(CancellationToken ct)
    {
        var access = grant ?? throw new DeliveryException("Unauthorized");
        string path = Prefix + "packages/" + access.PackageId;
        var manifest = await Get<PackageManifest>(path + "/manifest", DeliveryLimits.MaxManifestBytes, access.Token, ct);
        DeliveryValidation.Validate(manifest, requirePackageId: true);
        if (manifest.PackageId != access.PackageId || manifest.CollectionRevision != access.CollectionRevision || manifest.CatalogHash != access.CatalogHash) throw new DeliveryException("StaleCatalog");
        var settings = await Get<SettingsSnapshot>(path + "/settings", Protocol.Limits.MaxModConfigsTotalBytes * 2 + DeliveryLimits.MaxManifestBytes, access.Token, ct);
        DeliveryValidation.Validate(settings, manifest); return (manifest, settings);
    }
    public async Task<DownloadedModpack> Download(PackageManifest manifest, SettingsSnapshot settings, string cache, IProgress<DeliveryProgress>? progress, CancellationToken ct)
    {
        DeliveryValidation.Validate(manifest, true); DeliveryValidation.Validate(settings, manifest);
        var access = grant ?? throw new DeliveryException("Unauthorized");
        if (access.PackageId != manifest.PackageId || access.CatalogHash != manifest.CatalogHash) throw new DeliveryException("StaleCatalog");
        InstallFiles.NoLinks(cache); Directory.CreateDirectory(cache);
        foreach (string old in Directory.EnumerateFiles(cache, "*.part.json"))
        {
            string name = Path.GetFileName(old)[..^10];
            if (!DeliveryValidation.Id(name) || name == manifest.PackageId) continue;
            InstallFiles.NoLinks(old); string fragment = old[..^5]; InstallFiles.NoLinks(fragment);
            if (File.Exists(fragment)) File.Delete(fragment); File.Delete(old);
        }
        string partial = Path.Combine(cache, manifest.PackageId + ".part"), metadata = partial + ".json";
        var identity = new ResumeIdentity(endpoint.Id, pins.KnownFingerprint(endpoint), manifest.PackageId, manifest.CatalogHash, manifest.ArchiveSha256, manifest.ArchiveBytes);
        if (File.Exists(partial))
        {
            bool matches;
            try { matches = File.Exists(metadata) && DeliveryJson.Read<ResumeIdentity>(InstallFiles.Read(metadata, DeliveryLimits.SimpleBodyBytes), DeliveryLimits.SimpleBodyBytes) == identity && new FileInfo(partial).Length <= manifest.ArchiveBytes; }
            catch (Exception ex) when (ex is DeliveryException or IOException) { matches = false; }
            if (!matches) { File.Delete(partial); if (File.Exists(metadata)) File.Delete(metadata); }
        }
        InstallFiles.NoLinks(partial); InstallFiles.Write(metadata, DeliveryJson.Write(identity));
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        InstallFiles.Space(cache, manifest.ArchiveBytes - offset + DeliveryLimits.SpareBytes);
        if (offset != manifest.ArchiveBytes || InstallFiles.Hash(partial) != manifest.ArchiveSha256)
        {
            if (offset == manifest.ArchiveBytes) { File.Delete(partial); offset = 0; }
            await Transfer(manifest, partial, offset, access.Token, progress, ct);
        }
        progress?.Report(new("Verify", manifest.ArchiveBytes, manifest.ArchiveBytes));
        if (InstallFiles.Hash(partial) != manifest.ArchiveSha256) { File.Delete(partial); throw new DeliveryException("HashMismatch"); }
        SafeModArchive.ValidateArchive(partial, manifest, ct);
        return new DownloadedModpack(manifest, settings, partial);
    }
    private async Task Transfer(PackageManifest manifest, string target, long offset, string token, IProgress<DeliveryProgress>? progress, CancellationToken ct)
    {
        using var request = Request(Prefix + "packages/" + manifest.PackageId + "/archive", token);
        if (offset > 0) { request.Headers.Range = new RangeHeaderValue(offset, null); request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"" + manifest.ArchiveSha256 + "\"")); }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
        {
            response.Dispose(); File.Delete(target);
            await Transfer(manifest, target, 0, token, progress, ct); return;
        }
        await Check(response, ct);
        long start = response.StatusCode == HttpStatusCode.OK ? 0 : offset;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || range.Unit != "bytes" || range.From != offset || range.To != manifest.ArchiveBytes - 1 || range.Length != manifest.ArchiveBytes) throw new DeliveryException("InvalidRange");
        }
        else if (response.StatusCode != HttpStatusCode.OK) throw new DeliveryException("InvalidRange");
        if (response.Headers.ETag?.Tag != "\"" + manifest.ArchiveSha256 + "\"" || response.Headers.ETag.IsWeak || response.Content.Headers.ContentEncoding.Count > 0
            || response.Content.Headers.ContentLength is { } size && size != manifest.ArchiveBytes - start) throw new DeliveryException("InvalidRange");
        await using var file = new FileStream(target, start == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None, DeliveryLimits.BufferBytes, FileOptions.Asynchronous);
        file.Position = start;
        await using var stream = await response.Content.ReadAsStreamAsync(ct); var buffer = new byte[DeliveryLimits.BufferBytes]; long count = start;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, ct); if (read == 0) break;
                if (count + read > manifest.ArchiveBytes) throw new DeliveryException("Limit");
                await file.WriteAsync(buffer.AsMemory(0, read), ct); count += read; progress?.Report(new("Download", count, manifest.ArchiveBytes));
            }
            if (count != manifest.ArchiveBytes) throw new IOException("incomplete mod package");
        }
        finally { await file.FlushAsync(CancellationToken.None); file.Flush(true); }
    }
    private async Task<T> Get<T>(string path, int limit, string? token, CancellationToken ct)
    {
        using var request = Request(path, token); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await Check(response, ct); return DeliveryJson.Read<T>(await Read(response.Content, limit, ct), limit);
    }
    private static HttpRequestMessage Request(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path); if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return request;
    }
    private static async Task Check(HttpResponseMessage response, CancellationToken ct)
    {
        if ((int)response.StatusCode is >= 300 and < 400) throw new DeliveryException("Redirect");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) throw new DeliveryException("Unavailable");
        if (!response.IsSuccessStatusCode)
        {
            DeliveryError? error = null;
            try { error = DeliveryJson.Read<DeliveryError>(await Read(response.Content, DeliveryLimits.SimpleBodyBytes, ct), DeliveryLimits.SimpleBodyBytes); }
            catch (DeliveryException) { }
            throw new DeliveryException(error?.Code ?? ((int)response.StatusCode).ToString(), retryable: error?.Retryable ?? false);
        }
    }
    private static async Task<byte[]> Read(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit || content.Headers.ContentEncoding.Count > 0) throw new DeliveryException("Limit");
        await using var stream = await content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { int read = await stream.ReadAsync(buffer, ct); if (read == 0) break; if (output.Length + read > limit) throw new DeliveryException("Limit"); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private sealed record ResumeIdentity(string Server, string? Fingerprint, string PackageId, string CatalogHash, string ArchiveSha256, long ArchiveBytes);
    public void Dispose() { grant = null; http.Dispose(); }
}
