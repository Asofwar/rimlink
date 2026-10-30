using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.HygieneFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Saves;

/// <summary>
/// Old versions on disk as deltas against a base (spec §4). <see cref="SaveFixtures.MakeSave"/> pads with random bytes seeded by the
/// size: two saves of one padding size are nearly identical, two of different sizes share nothing.
/// </summary>
public class SaveCompactionTests
{
    private const int Padding = 1_000_000;
    private const string Off = "false";

    private static ServerFixture Server() => new ServerFixture().WithSetting("Auth:AllowPassword", "true");

    /// <summary>A player who uploads saves, compacting after each.</summary>
    private sealed class Colony(ServerFixture fixture, LoginReply login)
    {
        public long Id => login.Player.PlayerId;
        public string Token => login.SessionToken;
        public Protocol.V1.Saves.SavesClient Saves { get; } = new(fixture.CreateChannel());
        public string Dir => Path.Combine(fixture.DataDir, "saves", Id.ToString());

        public string PathOf(string name) => Path.Combine(Dir, name);

        public bool Has(string name) => File.Exists(PathOf(name));

        public SaveForm Locate(int number) => fixture.Services.GetRequiredService<SaveFiles>().Locate(Id, number);

        public async Task<byte[]> Send(long tick, int padding = Padding)
        {
            byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova", tick, padding);
            await Upload(Saves, Token, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova", tick));
            await Compact(fixture, Id);
            return save;
        }

        /// <summary>What the delta stands for, read straight from the two files.</summary>
        public byte[] DeltaContent(int number, int baseNumber) =>
            SaveDelta.Decompress(Decompress(File.ReadAllBytes(PathOf($"base-{baseNumber}.rws.zst"))), File.ReadAllBytes(PathOf($"{number}.d{baseNumber}.zst")));
    }

    private static async Task<Colony> Join(ServerFixture fixture) => new(fixture, await fixture.RegisterAndLogin("nova", playerName: "Nova"));

    [Fact]
    public async Task AnOldVersionBecomesADeltaAndTheCurrentStaysFull()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1);

        byte[] second = await nova.Send(2);

        Assert.True(nova.Has("2.rws.zst"));
        Assert.True(nova.Has("1.d1.zst"));
        Assert.True(nova.Has("base-1.rws.zst"));
        Assert.False(nova.Has("1.rws.zst"));
        long deltaLength = new FileInfo(nova.PathOf("1.d1.zst")).Length;
        Assert.True(deltaLength < 10_000, $"delta is {deltaLength} bytes");
        Assert.Equal(second, (await Download(nova.Saves, nova.Token)).data);
    }

    [Fact]
    public async Task LaterVersionsShareTheBase()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1);
        await nova.Send(2);
        await nova.Send(3);

        Assert.True(nova.Has("1.d1.zst"));
        Assert.True(nova.Has("2.d1.zst"));
        Assert.True(nova.Has("3.rws.zst"));
        Assert.Single(Directory.GetFiles(nova.Dir, "base-*"));
    }

    [Fact]
    public async Task ADeltaPastTheRatioStartsANewBase()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1);
        byte[] second = await nova.Send(2);
        await nova.Send(3, padding: 2 * Padding);
        await nova.Send(4, padding: 2 * Padding);

        Assert.True(nova.Has("base-1.rws.zst"));
        Assert.True(nova.Has("base-3.rws.zst"));
        Assert.True(nova.Has("3.d3.zst"));
        Assert.Equal(Decompress(second), nova.DeltaContent(2, 1));
    }

    [Fact]
    public async Task ABaseOutlivesItsVersionAndGoesWithItsLastDelta()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1, padding: 200_000);
        for (int tick = 2; tick <= 13; tick++) await nova.Send(tick, padding: 400_000);

        Assert.Empty(Directory.GetFiles(nova.Dir, "1.*"));
        Assert.Empty(Directory.GetFiles(nova.Dir, "2.*"));
        Assert.False(nova.Has("base-1.rws.zst"));
        Assert.True(nova.Has("base-2.rws.zst"));
        Assert.Equal(2, Assert.IsType<SaveForm.Delta>(nova.Locate(4)).BaseNumber);
    }

    [Fact]
    public async Task RetentionStillPrunesBasesWhenCompactionIsOff()
    {
        string dataDir;
        using (var first = Server().KeepDataDir())
        {
            var before = await Join(first);
            await before.Send(1, padding: 200_000);
            await before.Send(2, padding: 200_000);
            Assert.True(before.Has("base-1.rws.zst"));
            dataDir = first.DataDir;
        }

        using var fixture = Server().WithSetting("Saves:CompactOldVersions", Off).WithDataDir(dataDir).WithoutWorld();
        var nova = await Join(fixture);
        for (int tick = 3; tick <= 13; tick++) await nova.Send(tick, padding: 200_000);

        Assert.False(nova.Has("base-1.rws.zst"));
    }

    [Fact]
    public async Task AVersionOverTheRawLimitStaysFull()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        byte[] raw = Decompress(MakeSave(ServerFixture.WorldSeed, "Nova", tick: 1));
        int at = raw.AsSpan().LastIndexOf(Encoding.UTF8.GetBytes("</game>"));
        byte[] huge = new byte[raw.Length + SaveDelta.MaxRawBytes + 1];
        raw.AsSpan(0, at).CopyTo(huge);
        huge.AsSpan(at, (int)SaveDelta.MaxRawBytes + 1).Fill((byte)' ');
        raw.AsSpan(at).CopyTo(huge.AsSpan(at + (int)SaveDelta.MaxRawBytes + 1));
        byte[] save = Compress(huge);
        await Upload(nova.Saves, nova.Token, save, HeaderFor(save, ServerFixture.WorldSeed, "Nova", tick: 1));

        await nova.Send(2, padding: 0);

        Assert.True(nova.Has("1.rws.zst"));
        Assert.Empty(Directory.GetFiles(nova.Dir, "base-*"));
    }

    [Fact]
    public async Task ADamagedBaseIsReplaced()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1);
        await nova.Send(2);
        File.WriteAllBytes(nova.PathOf("base-1.rws.zst"), [1, 2, 3]);

        await nova.Send(3);

        Assert.True(nova.Has("base-2.rws.zst"));
        Assert.True(nova.Has("2.d2.zst"));
    }

    [Fact]
    public async Task AStaleDeltaIsRedoneByTheNextPass()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1);
        byte[] second = await nova.Send(2);
        File.WriteAllBytes(nova.PathOf("2.d1.zst"), [1, 2, 3]);

        await nova.Send(3);

        Assert.Equal(Decompress(second), nova.DeltaContent(2, 1));
    }

    [Fact]
    public async Task TheCurrentVersionLosesAStaleDelta()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1);
        byte[] second = await nova.Send(2);
        File.WriteAllBytes(nova.PathOf("2.d1.zst"), [1, 2, 3]);

        await Compact(fixture, nova.Id);

        Assert.False(nova.Has("2.d1.zst"));
        Assert.Equal(second, File.ReadAllBytes(nova.PathOf("2.rws.zst")));
    }

    [Fact]
    public async Task ABaseWithoutDeltasGoesWithTheNextPass()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1);
        await nova.Send(2);
        File.WriteAllBytes(nova.PathOf("base-9.rws.zst"), [1, 2, 3]);

        await Compact(fixture, nova.Id);

        Assert.False(nova.Has("base-9.rws.zst"));
        Assert.True(nova.Has("base-1.rws.zst"));
    }

    [Fact]
    public async Task CompactionOffLeavesFullFiles()
    {
        using var fixture = Server().WithSetting("Saves:CompactOldVersions", Off);
        var nova = await Join(fixture);
        await nova.Send(1);
        await nova.Send(2);

        Assert.True(nova.Has("1.rws.zst"));
        Assert.True(nova.Has("2.rws.zst"));
        Assert.Empty(Directory.GetFiles(nova.Dir, "base-*"));
    }

    [Fact]
    public async Task ADownloadInFlightFinishes()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        byte[] first = await nova.Send(1);
        using var scope = fixture.Services.CreateScope();
        var (_, content) = (await scope.ServiceProvider.GetRequiredService<SaveStore>().OpenCurrent(nova.Id))!.Value;

        await using (content)
        {
            await nova.Send(2);
            using var read = new MemoryStream();
            await content.CopyToAsync(read);
            Assert.Equal(first, read.ToArray());
        }
        await nova.Send(3);

        Assert.False(nova.Has("1.rws.zst"));
    }

    private static async Task<bool> Rollback(ServerFixture fixture, long playerId, int number)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(playerId, number);
    }

    [Fact]
    public async Task RollbackToADeltaRestoresTheContent()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        byte[] first = await nova.Send(1);
        await nova.Send(2);

        Assert.True(await Rollback(fixture, nova.Id, 1));

        Assert.Equal(Decompress(first), Decompress((await Download(nova.Saves, nova.Token)).data));
        Assert.True(nova.Has("1.rws.zst"));
        Assert.False(nova.Has("1.d1.zst"));
        Assert.True(nova.Has("2.d1.zst"));
        Assert.False(nova.Has("2.rws.zst"));
        var row = await Db(fixture, db => db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == nova.Id && v.Number == 1));
        byte[] file = File.ReadAllBytes(nova.PathOf("1.rws.zst"));
        Assert.Equal(file.Length, row.Size);
        Assert.Equal(SHA256.HashData(file), row.Sha256);
    }

    [Fact]
    public async Task RollbackToADamagedDeltaIsRefused()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1);
        byte[] second = await nova.Send(2);
        File.WriteAllBytes(nova.PathOf("1.d1.zst"), File.ReadAllBytes(nova.PathOf("1.d1.zst"))[..^10]);

        Assert.False(await Rollback(fixture, nova.Id, 1));

        Assert.Equal(second, (await Download(nova.Saves, nova.Token)).data);
    }

    [Fact]
    public async Task RollbackWithoutTheBaseIsRefused()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        await nova.Send(1);
        byte[] second = await nova.Send(2);
        File.Delete(nova.PathOf("base-1.rws.zst"));

        Assert.False(await Rollback(fixture, nova.Id, 1));

        Assert.Equal(second, (await Download(nova.Saves, nova.Token)).data);
    }

    [Fact]
    public async Task DownloadRebuildsACurrentVersionLeftAsADelta()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        byte[] first = await nova.Send(1);
        await nova.Send(2);
        // A backup's database, older than the files packed after it.
        await Db(fixture, db => db.SaveVersions.Where(v => v.PlayerId == nova.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, v => v.Number == 1)));

        var (header, data) = await Download(nova.Saves, nova.Token);

        Assert.Equal(Decompress(first), Decompress(data));
        Assert.True(nova.Has("1.rws.zst"));
        Assert.Equal(new FileInfo(nova.PathOf("1.rws.zst")).Length, header.TotalSize);
    }

    [Fact]
    public async Task RollbackWorksAfterCompactionWasTurnedOff()
    {
        string dataDir;
        byte[] first;
        using (var before = Server().KeepDataDir())
        {
            var colony = await Join(before);
            first = await colony.Send(1);
            await colony.Send(2);
            dataDir = before.DataDir;
        }

        using var fixture = Server().WithSetting("Saves:CompactOldVersions", Off).WithDataDir(dataDir).WithoutWorld();
        var nova = await Join(fixture);

        Assert.True(await Rollback(fixture, nova.Id, 1));

        Assert.Equal(Decompress(first), Decompress((await Download(nova.Saves, nova.Token)).data));
    }

    [Fact]
    public async Task RollbacksAndUploadsKeepEveryVersionReadable()
    {
        using var fixture = Server();
        var nova = await Join(fixture);
        var uploaded = new Dictionary<int, byte[]>
        {
            [1] = await nova.Send(1),
            [2] = await nova.Send(2),
            [3] = await nova.Send(3),
        };
        Assert.True(await Rollback(fixture, nova.Id, 1));
        uploaded[4] = await nova.Send(4);
        Assert.True(await Rollback(fixture, nova.Id, 2));
        uploaded[5] = await nova.Send(5);

        foreach (var (number, save) in uploaded)
        {
            Assert.True(await Rollback(fixture, nova.Id, number));
            Assert.Equal(Decompress(save), Decompress((await Download(nova.Saves, nova.Token)).data));
        }
    }
}
