namespace Rimlink.Discord.Api;

/// <summary>
/// The last admin feed id on disk (<c>RimlinkBot:StateFile</c>), so a restarted bot resumes where it stopped instead of only
/// seeing new events. No file configured: nothing is kept.
/// </summary>
public sealed class FeedState(string? path, ILogger<FeedState> log)
{
    public string? Load()
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            string id = File.ReadAllText(path).Trim();
            return id.Length > 0 ? id : null;
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Feed state {Path} could not be read; starting with new events", path);
            return null;
        }
    }

    public void Save(string id)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir is not null) Directory.CreateDirectory(dir);
            string temp = path + ".tmp";
            File.WriteAllText(temp, id);
            File.Move(temp, path, overwrite: true); // a crash mid-write leaves the previous id, never half of one
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Feed state {Path} could not be written", path);
        }
    }
}
