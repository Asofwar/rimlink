using System.CommandLine;
using Rimlink.Server.Backup;

namespace Rimlink.Tools.Commands;

public static class BackupCommands
{
    public static Command Backup()
    {
        var data = new Option<string>("--data") { Description = "Server data directory", Required = true };
        var output = new Option<string>("--out") { Description = "Archive to write (default: <data>/backups/rimlink-<utc>.tar.zst)" };
        var command = new Command("backup", "Snapshot the database and saves into one tar.zst archive (safe while the server runs)") { data, output };
        command.SetAction(parse =>
        {
            string dataDir = parse.GetValue(data)!;
            string file = parse.GetValue(output) ?? Path.Combine(dataDir, "backups", $"rimlink-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar.zst");
            BackupArchive.Create(dataDir, file);
            Console.WriteLine($"backup written: {file} ({new FileInfo(file).Length / 1024} KB)");
        });
        return command;
    }

    public static Command Restore()
    {
        var data = new Option<string>("--data") { Description = "Server data directory (server must be stopped)", Required = true };
        var from = new Option<string>("--from") { Description = "Archive to restore", Required = true };
        var command = new Command("restore", "Replace the database and saves with the archive's contents") { data, from };
        command.SetAction(parse =>
        {
            BackupArchive.Restore(parse.GetValue(from)!, parse.GetValue(data)!);
            Console.WriteLine("restored");
        });
        return command;
    }
}
