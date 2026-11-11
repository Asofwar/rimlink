extern alias tools;
using System.CommandLine;
using Microsoft.Data.Sqlite;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Support;
using tools::Rimlink.Tools.Commands;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostAnchorCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rimlink-anchor-cli", Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "data");
    private string Anchor => Path.Combine(root, "anchor");

    public OutpostAnchorCliTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Anchor);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Provision_creates_one_empty_authority_store_and_never_replaces_it()
    {
        using var output = new StringWriter();
        Assert.Equal(0, await Provision(output));
        string file = Path.Combine(Anchor, "authority.db");
        Assert.Contains(file, output.ToString());
        using (var db = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False"))
        {
            db.Open();
            Assert.Null(OutpostAnchorStore.ReadCheckedEnvelope(db));
        }
        byte[] before = File.ReadAllBytes(file);

        using var again = new StringWriter();
        Assert.Equal(1, await Provision(again));
        Assert.Contains("error", again.ToString());
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Provision_refuses_a_running_server_and_an_unaccepted_storage_contract()
    {
        using (new FileStream(Path.Combine(Data, BackupArchive.LockFile), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            using var running = new StringWriter();
            Assert.Equal(1, await Provision(running));
            Assert.Contains("stop the server", running.ToString());
        }
        using var contract = new StringWriter();
        Assert.Equal(1, await Run(BackupCommands.Anchor(contract), "provision", "--data", Data, "--anchor", Anchor));
        Assert.False(File.Exists(Path.Combine(Anchor, "authority.db")));
    }

    private Task<int> Provision(TextWriter output) => Run(BackupCommands.Anchor(output), "provision", "--data", Data, "--anchor", Anchor,
        "--storage-contract", "sqlite-local-v1", "--qualified-linux-filesystem", LocalTestStorage.LinuxQualification(Data));

    private static Task<int> Run(Command command, params string[] args) => command.Parse(args).InvokeAsync();
}
