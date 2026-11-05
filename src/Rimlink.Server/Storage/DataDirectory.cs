namespace Rimlink.Server.Storage;

/// <summary>Layout of the server data directory. Every path is built from numeric ids, never from user input.</summary>
public sealed class DataDirectory
{
    public DataDirectory(string root) : this(root, createDirectories: true) { }

    private DataDirectory(string root, bool createDirectories)
    {
        Root = Path.GetFullPath(root);
        if (createDirectories)
            foreach (string dir in new[] { Root, SavesRoot, Logs, Tls, Archive, Backups }) Directory.CreateDirectory(dir);
    }

    // Offline proof validation must not introduce unjournalled application directories into an installed cut.
    internal static DataDirectory Existing(string root) => Directory.Exists(root)
        ? new(root, createDirectories: false) : throw new DirectoryNotFoundException("data-directory-missing");

    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "rimlink.db");
    public string SavesRoot => Path.Combine(Root, "saves");
    public string Logs => Path.Combine(Root, "logs");
    public string Tls => Path.Combine(Root, "tls");
    public string Archive => Path.Combine(Root, "archive");
    public string Backups => Path.Combine(Root, "backups");
    public string LockFile => Path.Combine(Root, "server.lock");

    public string Saves(long playerId)
    {
        string dir = Path.Combine(SavesRoot, playerId.ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
