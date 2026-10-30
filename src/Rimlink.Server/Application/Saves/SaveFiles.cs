using System.Security.Cryptography;
using Rimlink.Server.Storage;
using ZstdSharp;

namespace Rimlink.Server.Application.Saves;

/// <summary>How a save version lies on disk. The form is read from the file names, so a backup's database never has to agree with its files.</summary>
public abstract record SaveForm
{
    public sealed record Full(string Path) : SaveForm;
    public sealed record Delta(string Path, int BaseNumber, string BasePath) : SaveForm;
    public sealed record Missing : SaveForm;
}

/// <summary>
/// Save files under &lt;data&gt;/saves/&lt;playerId&gt;: &lt;n&gt;.rws.zst is a full version, &lt;n&gt;.d&lt;k&gt;.zst a delta against
/// base-&lt;k&gt;.rws.zst. Nothing else knows these names.
/// </summary>
public sealed class SaveFiles(DataDirectory data)
{
    private const string BasePattern = "base-*.rws.zst";
    private const string DeltaPattern = "*.d*.zst";

    /// <summary>Held by anything that changes which files a version has.</summary>
    public SemaphoreSlim PerStoreLock { get; } = new(1, 1);

    public string FullPath(long playerId, int number) => Path.Combine(data.Saves(playerId), $"{number}.rws.zst");

    public string DeltaPath(long playerId, int number, int baseNumber) => Path.Combine(data.Saves(playerId), $"{number}.d{baseNumber}.zst");

    public string BasePath(long playerId, int baseNumber) => Path.Combine(data.Saves(playerId), $"base-{baseNumber}.rws.zst");

    public SaveForm Locate(long playerId, int number)
    {
        string full = FullPath(playerId, number);
        if (File.Exists(full)) return new SaveForm.Full(full);
        foreach (string delta in Deltas(playerId, number))
        {
            int baseNumber = BaseNumberOf(delta);
            string reference = BasePath(playerId, baseNumber);
            if (File.Exists(reference)) return new SaveForm.Delta(delta, baseNumber, reference);
        }
        return new SaveForm.Missing();
    }

    public int? NewestBase(long playerId) =>
        Directory.EnumerateFiles(data.Saves(playerId), BasePattern).Select(path => (int?)NumberOfBase(path)).Max();

    /// <summary>The content of a full file or a base; null above <see cref="SaveDelta.MaxRawBytes"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a readable zstd stream.</exception>
    public byte[]? ReadRaw(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var decompressed = new DecompressionStream(file);
            using var raw = new MemoryStream();
            byte[] buffer = new byte[1 << 20];
            int read;
            while ((read = decompressed.Read(buffer)) > 0)
            {
                if (raw.Length + read > SaveDelta.MaxRawBytes) return null;
                raw.Write(buffer, 0, read);
            }
            return raw.ToArray();
        }
        catch (Exception ex) when (ex is ZstdException or EndOfStreamException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>Writes the version a delta stands for as a full file at <paramref name="target"/>.</summary>
    /// <exception cref="InvalidDataException">The delta or its base is damaged.</exception>
    public (long Size, byte[] Sha256) Rebuild(SaveForm.Delta delta, string target)
    {
        byte[] reference = ReadRaw(delta.BasePath) ?? throw new InvalidDataException($"base {delta.BaseNumber} is over the size limit");
        byte[] content = SaveDelta.Decompress(reference, File.ReadAllBytes(delta.Path));
        using var compressor = new Compressor(3);
        byte[] compressed = compressor.Wrap(content).ToArray();
        WriteAtomically(target, compressed);
        return (compressed.Length, SHA256.HashData(compressed));
    }

    /// <summary>Flushed before the rename: the caller may delete the only other copy right after.</summary>
    public void WriteAtomically(string target, ReadOnlySpan<byte> content)
    {
        string temp = Path.Combine(Path.GetDirectoryName(target)!, $"compact-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(content);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public void DeleteDeltas(long playerId, int number)
    {
        foreach (string delta in Deltas(playerId, number).ToList()) File.Delete(delta);
    }

    /// <summary>Every form of the version; its base stays for <see cref="PruneBases"/>.</summary>
    public void Delete(long playerId, int number)
    {
        string full = FullPath(playerId, number);
        if (File.Exists(full)) File.Delete(full);
        DeleteDeltas(playerId, number);
    }

    /// <summary>Removes the bases no delta in the directory names. One that cannot go now (a backup is reading it) goes with a later pass.</summary>
    public void PruneBases(long playerId)
    {
        string dir = data.Saves(playerId);
        var used = Directory.EnumerateFiles(dir, DeltaPattern).Select(BaseNumberOf).ToHashSet();
        foreach (string path in Directory.EnumerateFiles(dir, BasePattern).ToList())
        {
            if (used.Contains(NumberOfBase(path))) continue;
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    /// <summary>What saves/ takes on disk; a file gone in the middle of the count is skipped.</summary>
    public long DiskBytes()
    {
        long total = 0;
        foreach (var file in new DirectoryInfo(data.SavesRoot).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { total += file.Length; }
            catch (FileNotFoundException) { }
        }
        return total;
    }

    private IEnumerable<string> Deltas(long playerId, int number) => Directory.EnumerateFiles(data.Saves(playerId), $"{number}.d*.zst");

    /// <summary>The k of &lt;n&gt;.d&lt;k&gt;.zst.</summary>
    private static int BaseNumberOf(string deltaPath) => int.Parse(Path.GetFileName(deltaPath).Split('.')[1][1..]);

    /// <summary>The k of base-&lt;k&gt;.rws.zst.</summary>
    private static int NumberOfBase(string basePath) => int.Parse(Path.GetFileName(basePath).Split('.')[0]["base-".Length..]);
}
