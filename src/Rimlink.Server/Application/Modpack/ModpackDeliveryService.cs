using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Modpack;

public sealed class ModpackDeliveryService(RimlinkDbContext db, ModpackService mods, DeliveryRuntime runtime, DataDirectory data,
    IOptions<ServerOptions> server, TimeProvider clock, AdminAudit audit)
{
    private string Root => Path.Combine(data.Root, "modpack-delivery");
    public string ArchivePath(string id)
    {
        if (!DeliveryValidation.Id(id)) throw new DeliveryException("NotFound");
        return Path.Combine(Root, "packages", id + ".zip");
    }
    private string PartialPath(string id) => Path.Combine(Root, "staging", id + ".part");
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Json<T>(T value) => Encoding.UTF8.GetString(DeliveryJson.Write(value));

    public async Task<CollectionDescriptor> Requirements(CancellationToken ct = default)
    {
        await runtime.CatalogLock.WaitAsync(ct);
        try { return await Describe(); } finally { runtime.CatalogLock.Release(); }
    }
    private async Task<CollectionDescriptor> Describe() => DeliveryValidation.Describe(DeliveryLimits.GameVersion,
        (await mods.Get()).Select(DeliveryEntry.From), (await mods.GetConfigs()).Select(c => new SharedSettingHash(c.PackageId, c.Handle, c.Hash)));

    public async Task<UploadAccepted> Offer(UploadOffer offer, Actor actor, CancellationToken ct)
    {
        if (offer.Manifest is null || !offer.RedistributionConfirmed || string.IsNullOrEmpty(server.Value.ServerPassword) && !offer.PublicDistributionConfirmed) throw new DeliveryException("Consent");
        DeliveryValidation.Validate(offer.Manifest);
        await runtime.CatalogLock.WaitAsync(ct);
        try
        {
            var current = await Describe(); Match(offer.Manifest, current);
            await Clean();
            if (await db.Set<ModpackPackage>().AnyAsync(p => !p.Verified && p.UploadExpiresAt > Now, ct)) throw new DeliveryException("Busy", retryable: true);
            // One candidate also covers a verified upload awaiting publication.
            var publication = await db.Set<ModpackPublication>().SingleOrDefaultAsync(ct);
            if (await db.Set<ModpackPackage>().AnyAsync(p => p.UploadExpiresAt > Now && p.Id != (publication == null ? null : publication.PackageId) && p.Id != (publication == null ? null : publication.PreviousPackageId), ct)) throw new DeliveryException("Busy", retryable: true);
            Directory.CreateDirectory(Path.Combine(Root, "staging")); Directory.CreateDirectory(Path.Combine(Root, "packages"));
            CheckSpace(offer.Manifest.ArchiveBytes + DeliveryLimits.SpareBytes);
            string id = Guid.NewGuid().ToString("N");
            var row = new ModpackPackage { Id = id, ManifestJson = Json(offer.Manifest with { PackageId = id }), Creator = actor.Name, CreatedAt = Now, UploadExpiresAt = Now + DeliveryLimits.AccessLifetime,
                RedistributionConfirmed = true, PublicDistributionConfirmed = offer.PublicDistributionConfirmed };
            db.Add(row); await db.SaveChangesAsync(ct);
            return new UploadAccepted(id, row.UploadExpiresAt);
        }
        finally { runtime.CatalogLock.Release(); }
    }
    public async Task Upload(string id, Stream body, long? length, string? contentType, CancellationToken ct)
    {
        if (!DeliveryValidation.Id(id)) throw new DeliveryException("NotFound");
        if (contentType?.Split(';')[0].Trim() != "application/zip") throw new DeliveryException("InvalidArchive");
        if (!await runtime.UploadLock.WaitAsync(0, ct)) throw new DeliveryException("Busy", retryable: true);
        try
        {
            var row = await db.Set<ModpackPackage>().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new DeliveryException("NotFound");
            var manifest = DeliveryJson.Read<PackageManifest>(Encoding.UTF8.GetBytes(row.ManifestJson));
            if (length is { } bytes && bytes != manifest.ArchiveBytes) throw new DeliveryException("Limit");
            string target = ArchivePath(id), partial = PartialPath(id);
            bool ready = row.Verified && File.Exists(target);
            if (!ready && row.UploadExpiresAt <= Now) throw new DeliveryException("Expired");
            CheckSpace(manifest.ArchiveBytes + DeliveryLimits.SpareBytes);
            try
            {
                await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, DeliveryLimits.BufferBytes, FileOptions.Asynchronous))
                {
                    var buffer = new byte[DeliveryLimits.BufferBytes]; long count = 0;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    while (true)
                    {
                        int read = await body.ReadAsync(buffer, ct); if (read == 0) break;
                        count += read; if (count > manifest.ArchiveBytes) throw new DeliveryException("Limit");
                        hash.AppendData(buffer, 0, read); await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    if (count != manifest.ArchiveBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != manifest.ArchiveSha256) throw new DeliveryException("HashMismatch");
                    await file.FlushAsync(ct); file.Flush(flushToDisk: true);
                }
                SafeModArchive.ValidateArchive(partial, manifest, ct);
                if (ready) return;
                File.Move(partial, target, overwrite: true);
                row.Verified = true; await db.SaveChangesAsync(ct);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        finally { runtime.UploadLock.Release(); }
    }
    public async Task<DeliveryPublication> Publish(string id, PublishPackage request, Actor actor, CancellationToken ct)
    {
        if (!DeliveryValidation.Id(id)) throw new DeliveryException("NotFound");
        await runtime.CatalogLock.WaitAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.Set<ModpackPackage>().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new DeliveryException("NotFound");
            var manifest = DeliveryJson.Read<PackageManifest>(Encoding.UTF8.GetBytes(row.ManifestJson));
            var descriptor = await Describe(); Match(manifest, descriptor);
            if (request.ExpectedCollectionRevision != descriptor.Revision || request.ExpectedCatalogHash != descriptor.CatalogHash) throw new DeliveryException("StaleCatalog");
            if (!row.Verified || !File.Exists(ArchivePath(id))) throw new DeliveryException("Unavailable");
            if (!row.RedistributionConfirmed || string.IsNullOrEmpty(server.Value.ServerPassword) && !row.PublicDistributionConfirmed) throw new DeliveryException("Consent");
            var configs = (await mods.GetConfigs()).Select(c => new SharedConfig(c.PackageId, c.Handle, c.Hash, c.Content.ToByteArray())).ToArray();
            if (configs.Length > 0 && !request.SharedSettingsConfirmed) throw new DeliveryException("SettingsConsent");
            var settings = new SettingsSnapshot(id, descriptor.CatalogHash, configs); DeliveryValidation.Validate(settings, manifest);
            var publication = await db.Set<ModpackPublication>().SingleOrDefaultAsync(ct);
            if (publication?.PackageId == id) return new DeliveryPublication(true, id, descriptor.Revision, descriptor.CatalogHash);
            if (row.UploadExpiresAt <= Now) throw new DeliveryException("Expired");
            row.SettingsJson = Json(settings); row.SharedSettingsConfirmed = request.SharedSettingsConfirmed;
            if (publication is null) { publication = new ModpackPublication(); db.Add(publication); }
            publication.PreviousPackageId = publication.PackageId; publication.PackageId = id;
            var action = audit.Stage(actor, "modpack.package.publish", null, id);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            runtime.Revoke(); await audit.Publish(action); await Clean();
            return new DeliveryPublication(true, id, descriptor.Revision, descriptor.CatalogHash);
        }
        finally { runtime.CatalogLock.Release(); }
    }
    public async Task<DeliveryPublication> Unpublish(Actor actor, CancellationToken ct)
    {
        await runtime.CatalogLock.WaitAsync(ct);
        try
        {
            var publication = await db.Set<ModpackPublication>().SingleOrDefaultAsync(ct);
            if (publication?.PackageId is { } id)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                publication.PreviousPackageId = id; publication.PackageId = null;
                var action = audit.Stage(actor, "modpack.package.unpublish", null, id);
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); runtime.Revoke(); await audit.Publish(action);
            }
            return new DeliveryPublication(false);
        }
        finally { runtime.CatalogLock.Release(); }
    }
    public async Task<DeliveryPublication> Publication(CancellationToken ct)
    {
        await runtime.CatalogLock.WaitAsync(ct);
        try { var row = await Current(ct); if (row is null) return new(false); var p = Manifest(row); return new(true, p.PackageId, p.CollectionRevision, p.CatalogHash); }
        finally { runtime.CatalogLock.Release(); }
    }
    public async Task<AccessGrant> Access(AccessRequest request, CancellationToken ct)
    {
        await runtime.CatalogLock.WaitAsync(ct);
        try
        {
            long epoch = runtime.Epoch;
            string? password = server.Value.ServerPassword;
            byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(request.ServerPassword ?? "")), expected = SHA256.HashData(Encoding.UTF8.GetBytes(password ?? ""));
            if (!CryptographicOperations.FixedTimeEquals(supplied, expected)) throw new DeliveryException("Unauthorized");
            var row = await Current(ct) ?? throw new DeliveryException("Unavailable"); var p = Manifest(row);
            if (request.ExpectedPackageId is { } id && id != p.PackageId) throw new DeliveryException("StaleCatalog");
            return runtime.Issue(p, epoch);
        }
        finally { runtime.CatalogLock.Release(); }
    }
    public async Task<(PackageManifest Manifest, SettingsSnapshot Settings, DeliveryRuntime.Grant Grant)> Authorize(string token, string id, CancellationToken ct)
    {
        var grant = runtime.Find(token, id);
        await runtime.CatalogLock.WaitAsync(ct);
        try
        {
            var row = await Current(ct); if (row?.Id != id) throw new DeliveryException("Unauthorized");
            runtime.Check(grant); var p = Manifest(row);
            if (p.CatalogHash != grant.Catalog) throw new DeliveryException("Unauthorized");
            return (p, DeliveryJson.Read<SettingsSnapshot>(Encoding.UTF8.GetBytes(row.SettingsJson!), SettingsLimit), grant);
        }
        finally { runtime.CatalogLock.Release(); }
    }
    public const int SettingsLimit = Protocol.Limits.MaxModConfigsTotalBytes * 2 + DeliveryLimits.MaxManifestBytes;
    private static PackageManifest Manifest(ModpackPackage row) => DeliveryJson.Read<PackageManifest>(Encoding.UTF8.GetBytes(row.ManifestJson));
    private async Task<ModpackPackage?> Current(CancellationToken ct)
    {
        string? id = (await db.Set<ModpackPublication>().AsNoTracking().SingleOrDefaultAsync(ct))?.PackageId;
        if (id is null) return null;
        var row = await db.Set<ModpackPackage>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (row is null || !row.Verified || row.SettingsJson is null || !File.Exists(ArchivePath(id))) return null;
        var manifest = Manifest(row);
        if (!runtime.ArchiveMatches(ArchivePath(id), manifest.ArchiveSha256, manifest.ArchiveBytes) || string.IsNullOrEmpty(server.Value.ServerPassword) && !row.PublicDistributionConfirmed) return null;
        try { Match(manifest, await Describe()); } catch (DeliveryException) { runtime.Revoke(); return null; }
        return row;
    }
    private static void Match(PackageManifest manifest, CollectionDescriptor descriptor)
    {
        if (manifest.CollectionRevision != descriptor.Revision || manifest.CatalogHash != descriptor.CatalogHash) throw new DeliveryException("StaleCatalog");
    }
    private void CheckSpace(long bytes)
    {
        long stored = 0;
        foreach (string directory in new[] { Path.Combine(Root, "staging"), Path.Combine(Root, "packages") })
            if (Directory.Exists(directory))
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new DeliveryException("UnsafePath");
                    stored += new FileInfo(file).Length;
                    if (stored + bytes > DeliveryLimits.MaxServerStorageBytes) throw new DeliveryException("DiskSpace");
                }
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root))!).AvailableFreeSpace < bytes) throw new DeliveryException("DiskSpace");
    }
    private async Task Clean()
    {
        // Never remove a file an active transfer still owns, even on platforms that allow unlinking it.
        if (runtime.HasTransfers) return;
        if (!await runtime.UploadLock.WaitAsync(0)) return;
        try
        {
            var publication = await db.Set<ModpackPublication>().AsNoTracking().SingleOrDefaultAsync();
            var rows = await db.Set<ModpackPackage>().Where(p => p.UploadExpiresAt <= Now || p.Verified).ToListAsync();
            foreach (var row in rows.Where(p => p.Id != publication?.PackageId && p.Id != publication?.PreviousPackageId))
            {
                if (row.UploadExpiresAt > Now && row.SettingsJson is null) continue;
                if (File.Exists(ArchivePath(row.Id))) File.Delete(ArchivePath(row.Id));
                if (File.Exists(PartialPath(row.Id))) File.Delete(PartialPath(row.Id));
                db.Remove(row);
            }
            await db.SaveChangesAsync();
            var known = (await db.Set<ModpackPackage>().AsNoTracking().Select(p => p.Id).ToListAsync()).ToHashSet(StringComparer.Ordinal);
            foreach (string directory in new[] { Path.Combine(Root, "staging"), Path.Combine(Root, "packages") })
                if (Directory.Exists(directory))
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        string extension = Path.GetExtension(file), id = Path.GetFileNameWithoutExtension(file);
                        if (extension is not (".zip" or ".part") || !DeliveryValidation.Id(id) || known.Contains(id) || File.GetLastWriteTimeUtc(file) > Now - DeliveryLimits.AccessLifetime) continue;
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new DeliveryException("UnsafePath");
                        File.Delete(file);
                    }
        }
        finally { runtime.UploadLock.Release(); }
    }
}
