using System.CommandLine;
using Rimlink.Tools.Commands;

var root = new RootCommand("Rimlink server tools: backups, modpack import, admin commands over the REST API");
root.Subcommands.Add(BackupCommands.Backup());
root.Subcommands.Add(BackupCommands.Restore());
root.Subcommands.Add(ModpackCommand.Build());
root.Subcommands.Add(AdminCommand.Build());
return await root.Parse(args).InvokeAsync();
