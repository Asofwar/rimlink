using System.Security.Cryptography;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Xunit;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Saves;

public class SaveFilesTests : IDisposable
{
    private const long Player = 7;

    private readonly string root = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));
    private readonly SaveFiles files;

    public SaveFilesTests() => files = new SaveFiles(new DataDirectory(root));

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Touch(string name, long player = Player)
    {
        string dir = Path.Combine(root, "saves", player.ToString());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    /// <summary>A real base and delta for version 2, returning the content the delta stands for.</summary>
    private byte[] MakeDelta()
    {
        byte[] reference = Decompress(MakeSave("seed", "Nova", tick: 1, paddingBytes: 100_000));
        byte[] content = Decompress(MakeSave("seed", "Nova", tick: 2, paddingBytes: 100_000));
        Touch("base-1.rws.zst");
        File.WriteAllBytes(files.BasePath(Player, 1), Compress(reference));
        Touch("2.d1.zst");
        File.WriteAllBytes(files.DeltaPath(Player, 2, 1), SaveDelta.Compress(reference, content));
        return content;
    }

    [Fact]
    public void LocatePrefersTheFullFile()
    {
        string full = Touch("1.rws.zst");
        Touch("base-1.rws.zst");
        Touch("1.d1.zst");

        Assert.Equal(full, Assert.IsType<SaveForm.Full>(files.Locate(Player, 1)).Path);
    }

    [Fact]
    public void LocateFindsADeltaWithItsBase()
    {
        string delta = Touch("2.d1.zst");
        string reference = Touch("base-1.rws.zst");

        var form = Assert.IsType<SaveForm.Delta>(files.Locate(Player, 2));

        Assert.Equal((delta, 1, reference), (form.Path, form.BaseNumber, form.BasePath));
    }

    [Fact]
    public void ADeltaWithoutItsBaseIsMissing()
    {
        Touch("2.d1.zst");

        Assert.IsType<SaveForm.Missing>(files.Locate(Player, 2));
    }

    [Fact]
    public void LocateDoesNotConfuseNumbers()
    {
        Touch("11.d3.zst");
        Touch("base-3.rws.zst");

        Assert.IsType<SaveForm.Missing>(files.Locate(Player, 1));
    }

    [Fact]
    public void DeleteRemovesEveryForm()
    {
        string full = Touch("1.rws.zst");
        string delta = Touch("1.d1.zst");
        string other = Touch("11.d1.zst");

        files.Delete(Player, 1);

        Assert.False(File.Exists(full));
        Assert.False(File.Exists(delta));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void PruneKeepsBasesThatADeltaNames()
    {
        string used = Touch("base-1.rws.zst");
        Touch("2.d1.zst");
        string unused = Touch("base-3.rws.zst");

        files.PruneBases(Player);

        Assert.True(File.Exists(used));
        Assert.False(File.Exists(unused));
    }

    [Fact]
    public void NewestBaseIsTheHighestNumber()
    {
        Assert.Null(files.NewestBase(Player));

        Touch("base-2.rws.zst");
        Touch("base-10.rws.zst");

        Assert.Equal(10, files.NewestBase(Player));
    }

    [Fact]
    public void RebuildRestoresTheContent()
    {
        byte[] content = MakeDelta();
        var delta = Assert.IsType<SaveForm.Delta>(files.Locate(Player, 2));
        string target = files.FullPath(Player, 2);

        var (size, sha256) = files.Rebuild(delta, target);

        Assert.Equal(new FileInfo(target).Length, size);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(target)), sha256);
        Assert.Equal(content, files.ReadRaw(target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.tmp"));
    }

    [Fact]
    public void RebuildOfADamagedDeltaLeavesNoTarget()
    {
        MakeDelta();
        var delta = Assert.IsType<SaveForm.Delta>(files.Locate(Player, 2));
        File.WriteAllBytes(delta.Path, File.ReadAllBytes(delta.Path)[..^10]);
        string target = files.FullPath(Player, 2);

        Assert.Throws<InvalidDataException>(() => files.Rebuild(delta, target));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public void ReadRawOfAnUnreadableFileIsInvalidData()
    {
        string path = Touch("1.rws.zst");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => files.ReadRaw(path));
    }

    [Fact]
    public void ReadRawIsNullAboveTheLimit()
    {
        string path = Touch("1.rws.zst");
        File.WriteAllBytes(path, Compress(new byte[SaveDelta.MaxRawBytes + 1]));

        Assert.Null(files.ReadRaw(path));
    }

    [Fact]
    public void DiskBytesSumsTheTree()
    {
        Touch("1.rws.zst");
        Touch("1.rws.zst", player: 8);

        Assert.Equal(2, files.DiskBytes());
    }
}
