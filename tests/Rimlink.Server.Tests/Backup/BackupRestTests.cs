using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class BackupRestTests
{
    private readonly ManualTimeProvider clock = new();

    private ServerFixture Server() => new ServerFixture()
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithServices(s => s.AddSingleton<TimeProvider>(clock));

    [Fact]
    public async Task CreateListAndDownload()
    {
        using var fixture = Server();
        var rest = fixture.Rest();

        var created = await rest.PostAsync("/api/admin/backups", null);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        string name = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
        Assert.Matches(BackupService.NamePattern, name);

        var list = await rest.GetFromJsonAsync<JsonElement>("/api/admin/backups");
        Assert.Contains(list.EnumerateArray(), b => b.GetProperty("name").GetString() == name);

        byte[] downloaded = await rest.GetByteArrayAsync($"/api/admin/backups/{name}");
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture.DataDir, "backups", name)), downloaded);

        var actions = (await rest.GetFromJsonAsync<JsonElement>("/api/admin/actions")).EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("backup", actions);
        Assert.Contains("backup-download", actions);
    }

    [Theory]
    [InlineData("rimlink.db")]
    [InlineData("..%2Frimlink.db")]
    [InlineData("rimlink-20261029-120000.tar.zst")]
    public async Task BadNamesAre404(string name)
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Rest().GetAsync($"/api/admin/backups/{name}")).StatusCode);
    }

    [Fact]
    public async Task SecondBackupInTheSameSecondIs409()
    {
        using var fixture = Server();
        var rest = fixture.Rest();

        Assert.Equal(HttpStatusCode.OK, (await rest.PostAsync("/api/admin/backups", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await rest.PostAsync("/api/admin/backups", null)).StatusCode);
    }

    [Fact]
    public async Task BackupWhileOneRunsIs409()
    {
        using var fixture = Server();
        var rest = fixture.Rest();
        var service = fixture.Services.GetRequiredService<BackupService>();

        using (var gate = service.TryAcquireGate())
        {
            Assert.NotNull(gate);
            Assert.Equal(HttpStatusCode.Conflict, (await rest.PostAsync("/api/admin/backups", null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await rest.PostAsync("/api/admin/backups", null)).StatusCode);
    }

    [Fact]
    public void PruneSkipsAFileItCannotDelete()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string[] names = ["rimlink-20261029-100000.tar.zst", "rimlink-20261029-110000.tar.zst", "rimlink-20261029-120000.tar.zst"];
            for (int i = 0; i < names.Length; i++)
            {
                string path = Path.Combine(dir, names[i]);
                File.WriteAllText(path, names[i]);
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 29, 10 + i, 0, 0, DateTimeKind.Utc));
            }

            using (new FileStream(Path.Combine(dir, names[0]), FileMode.Open, FileAccess.Read, FileShare.None))
                BackupScheduler.Prune(dir, keep: 1);

            Assert.True(File.Exists(Path.Combine(dir, names[2])));
            Assert.False(File.Exists(Path.Combine(dir, names[1])));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
