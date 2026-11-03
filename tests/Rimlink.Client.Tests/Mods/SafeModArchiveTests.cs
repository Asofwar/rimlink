using System.IO.Compression;
using System.Security.Cryptography;
using Rimlink.Protocol;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Mods;

public sealed class SafeModArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rl-zip-" + Guid.NewGuid().ToString("N"));
    private const string Folder = "00000000000000000000000000000001";
    public SafeModArchiveTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void APreparedCollectionPacksAndExtractsWithoutChangingOriginals()
    {
        string source = Path.Combine(root, "source"), mod = Path.Combine(source, "demo");
        Directory.CreateDirectory(Path.Combine(mod, "About"));
        File.WriteAllText(Path.Combine(mod, "About", "About.xml"), "<ModMetaData><packageId>test.mod</packageId><name>Demo</name><modVersion>2</modVersion><supportedVersions><li>1.7</li></supportedVersions><modDependencies><li><packageId>ludeon.rimworld</packageId></li></modDependencies></ModMetaData>");
        Directory.CreateDirectory(Path.Combine(mod, "Defs"));
        File.WriteAllText(Path.Combine(mod, "Defs", "a.xml"), "<Defs/>");
        Directory.CreateDirectory(Path.Combine(mod, "Textures", "empty"));
        var collection = DeliveryValidation.Describe("1.7", [new(0, "ludeon.rimworld", "", "", EntryKind.Required), new(1, "test.mod", "2", ModContent.Scan(mod).FilesHash, EntryKind.Required)], []);
        var packed = SafeModArchive.PackPreparedCollection(source, collection, Path.Combine(root, "packed"));
        SafeModArchive.ValidateArchive(packed.ArchivePath, packed.Manifest);
        string destination = Path.Combine(root, "extracted");
        SafeModArchive.ExtractValidated(packed.ArchivePath, packed.Manifest, destination);
        string extracted = Path.Combine(destination, "mods", Assert.Single(packed.Manifest.Mods).FolderId);
        Assert.Equal(ModContent.Scan(mod), ModContent.Scan(extracted));
        Assert.True(Directory.Exists(Path.Combine(extracted, "Textures", "empty")));
        Assert.True(File.Exists(Path.Combine(mod, "About", "About.xml")));
        Assert.Throws<DeliveryException>(() => SafeModArchive.ExtractValidated(packed.ArchivePath, packed.Manifest, destination));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("mods/00000000000000000000000000000001/../outside")]
    [InlineData("mods/00000000000000000000000000000001/a:stream")]
    [InlineData("mods/00000000000000000000000000000001/CON.txt")]
    [InlineData("mods/00000000000000000000000000000001/a.")]
    [InlineData("mods/00000000000000000000000000000001/Config/Prefs.xml")]
    [InlineData("mods/00000000000000000000000000000001/Assembly-CSharp.dll")]
    public void UnsafeEntryNeverWritesOutsideStaging(string name)
    {
        string zip = CreateZip([(name, "data", 0)]);
        var manifest = Manifest(zip, 4, 1);
        Assert.Throws<DeliveryException>(() => SafeModArchive.ExtractValidated(zip, manifest, Path.Combine(root, "stage")));
        Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
    }

    [Fact]
    public void LinksCaseCollisionsAndCompressionBombsAreRejected()
    {
        string link = CreateZip([("mods/" + Folder + "/link", "../outside", unchecked((int)0xA1FF0000))]);
        Assert.Throws<DeliveryException>(() => SafeModArchive.ValidateArchive(link, Manifest(link, 10, 1)));
        string collision = CreateZip([("mods/" + Folder + "/a", "x", 0), ("mods/" + Folder + "/A", "y", 0)]);
        Assert.Throws<DeliveryException>(() => SafeModArchive.ValidateArchive(collision, Manifest(collision, 2, 2)));
        string bomb = CreateZip([("mods/" + Folder + "/data", new string('a', 200_000), 0)]);
        Assert.Throws<DeliveryException>(() => SafeModArchive.ValidateArchive(bomb, Manifest(bomb, 200_000, 1)));
    }

    [Theory]
    [InlineData("mods/00000000000000000000000000000001/1.7/Config/Prefs.xml", "ForbiddenContent")]
    [InlineData("mods/00000000000000000000000000000001/nested/.git/config", "ForbiddenContent")]
    [InlineData("mods/00000000000000000000000000000001/CONIN$", "UnsafePath")]
    [InlineData("mods/00000000000000000000000000000001/CONOUT$", "UnsafePath")]
    public void PrivateDirectoriesAndConsoleDevicesAreRejectedBeforeAnyExtraction(string name, string code)
    {
        string zip = CreateZip([(name, "data", 0)]), stage = Path.Combine(root, "stage");
        Assert.Equal(code, Assert.Throws<DeliveryException>(() => SafeModArchive.ExtractValidated(zip, Manifest(zip, 4, 1), stage)).Code);
        Assert.False(Directory.Exists(stage));
    }

    private string CreateZip((string Name, string Content, int Attributes)[] files)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal); entry.ExternalAttributes = file.Attributes;
            using var writer = new StreamWriter(entry.Open()); writer.Write(file.Content);
        }
        return path;
    }

    private static PackageManifest Manifest(string zip, long bytes, int files)
    {
        var collection = DeliveryValidation.Describe("1.7", [new(0, "ludeon.rimworld", "", "", EntryKind.Required), new(1, "test.mod", "2", "", EntryKind.Required)], []);
        using var stream = File.OpenRead(zip);
        return new PackageManifest { GameVersion = "1.7", Entries = collection.Entries, CollectionRevision = collection.Revision, CatalogHash = collection.CatalogHash,
            ArchiveBytes = stream.Length, ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(stream)), ExpandedBytes = bytes, FileCount = files,
            Prerequisites = [collection.Entries[0]], Mods = [new DeliveryMod { PackageId = "test.mod", Version = "2", DisplayName = "Demo", FilesHash = new string('a', 64), ContentHash = new string('b', 64), FolderId = Folder, Position = 1, SupportedVersions = ["1.7"], FileCount = files, ExpandedBytes = bytes }] };
    }
}
