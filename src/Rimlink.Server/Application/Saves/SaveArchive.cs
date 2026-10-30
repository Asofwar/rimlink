using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Saves;

/// <summary>Copies a player's save files into an archive directory with a manifest of their versions, and back.</summary>
public sealed class SaveArchive(DataDirectory data)
{
    public const string ManifestFile = "manifest.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>One save version; the integrity marks default for manifests written before they existed.</summary>
    public sealed record ManifestVersion(int Number, long GameTick, long Size, string Sha256, DateTime CreatedAt, bool IsCurrent,
        bool DevMode = false, long WealthTotal = 0, long WealthItems = 0);

    public sealed record Manifest(long PlayerId, List<ManifestVersion> Versions);

    private string PathOf(long playerId, int number) => Path.Combine(data.Saves(playerId), $"{number}.rws.zst");

    private static string FileName(int number) => $"{number}.rws.zst";

    public void EnsureFilesExist(long playerId, IEnumerable<SaveVersion> versions)
    {
        foreach (var v in versions)
            if (!File.Exists(PathOf(playerId, v.Number))) throw new FileNotFoundException($"save v{v.Number} of player {playerId} is missing", PathOf(playerId, v.Number));
    }

    /// <summary>Copies every version into <paramref name="dir"/>, checks each copy against its recorded hash, writes the manifest.</summary>
    public async Task CopyOut(IReadOnlyList<SaveVersion> versions, string dir)
    {
        Directory.CreateDirectory(dir);
        var manifest = new Manifest(versions.FirstOrDefault()?.PlayerId ?? 0, []);
        foreach (var v in versions)
        {
            string target = Path.Combine(dir, FileName(v.Number));
            File.Copy(PathOf(v.PlayerId, v.Number), target, overwrite: true);
            byte[] hash = await HashOf(target);
            if (!hash.AsSpan().SequenceEqual(v.Sha256)) throw new InvalidDataException($"save v{v.Number} of player {v.PlayerId} does not match its hash");
            manifest.Versions.Add(new ManifestVersion(v.Number, v.GameTick, v.Size, Convert.ToHexString(v.Sha256), v.CreatedAt, v.IsCurrent,
                v.DevMode, v.WealthTotal, v.WealthItems));
        }
        await File.WriteAllTextAsync(Path.Combine(dir, ManifestFile), JsonSerializer.Serialize(manifest, Json));
    }

    /// <summary>
    /// Verifies every archived file against the manifest, then copies them back into the player's save directory.
    /// Null when the manifest or a file is missing, a hash differs, or the files cannot be read or written (copies already
    /// made are left for the next store to overwrite; no version row points at them).
    /// </summary>
    public async Task<List<SaveVersion>?> CopyIn(long playerId, string dir)
    {
        try
        {
            return await CopyInChecked(playerId, dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<List<SaveVersion>?> CopyInChecked(long playerId, string dir)
    {
        string manifestPath = Path.Combine(dir, ManifestFile);
        if (!File.Exists(manifestPath)) return null;
        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (manifest is null) return null;

        foreach (var v in manifest.Versions)
        {
            string source = Path.Combine(dir, FileName(v.Number));
            if (!File.Exists(source) || Convert.ToHexString(await HashOf(source)) != v.Sha256) return null;
        }

        var restored = new List<SaveVersion>();
        foreach (var v in manifest.Versions)
        {
            File.Copy(Path.Combine(dir, FileName(v.Number)), PathOf(playerId, v.Number), overwrite: true);
            restored.Add(new SaveVersion
            {
                PlayerId = playerId,
                Number = v.Number,
                GameTick = v.GameTick,
                Size = v.Size,
                Sha256 = Convert.FromHexString(v.Sha256),
                CreatedAt = v.CreatedAt,
                IsCurrent = v.IsCurrent,
                DevMode = v.DevMode,
                WealthTotal = v.WealthTotal,
                WealthItems = v.WealthItems,
            });
        }
        return restored;
    }

    public void DeleteFiles(long playerId, IEnumerable<int> numbers)
    {
        foreach (int number in numbers)
        {
            string path = PathOf(playerId, number);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<byte[]> HashOf(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }
}
