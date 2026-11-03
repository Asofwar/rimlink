using System.Text;
using Rimlink.Protocol;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Mods;

public sealed class DeliveryContractTests
{
    public static readonly string Files = new('a', 64);
    public static CollectionDescriptor Collection() => DeliveryValidation.Describe("1.7", [new(0, "ludeon.rimworld", "", "", EntryKind.Required), new(1, "Test.Mod_steam", "2", Files, EntryKind.Required)], []);

    [Fact]
    public void CollectionNormalizesIdentityAndBindsOrderAndSettings()
    {
        var first = Collection();
        Assert.Equal("test.mod", first.Entries[1].PackageId);
        var again = DeliveryValidation.Describe("1.7", [new(1, "test.mod", "2", Files, EntryKind.Required), first.Entries[0]], []);
        Assert.Equal(first.Revision, again.Revision);
        var changed = DeliveryValidation.Describe("1.7", [first.Entries[0], first.Entries[1] with { Position = 3 }], []);
        Assert.NotEqual(first.Revision, changed.Revision);
        var settings = DeliveryValidation.Describe("1.7", first.Entries, [new("test.mod", "Settings", ModContent.SettingsHash("<x/>"u8.ToArray()))]);
        Assert.Equal(first.Revision, settings.Revision);
        Assert.NotEqual(first.CatalogHash, settings.CatalogHash);
    }

    [Fact]
    public void DuplicateIdsUnknownVersionsAndOversizedJsonAreRejected()
    {
        Assert.Throws<DeliveryException>(() => DeliveryValidation.Describe("1.7", [new(0, "test.mod", "", Files, EntryKind.Required), new(1, "TEST.MOD_steam", "", Files, EntryKind.Required)], []));
        Assert.Throws<DeliveryException>(() => DeliveryValidation.Validate(new PackageManifest { FormatVersion = 2 }));
        Assert.Throws<DeliveryException>(() => DeliveryJson.Read<PackageManifest>(new byte[DeliveryLimits.MaxManifestBytes + 1]));
        Assert.Throws<DeliveryException>(() => DeliveryJson.Read<PackageManifest>(Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"archiveUrl\":\"file:///x\"}")));
    }

    [Fact]
    public void DuplicateJsonPropertiesDoNotSilentlyReplaceAConsentOrPassword()
    {
        Assert.Throws<DeliveryException>(() => DeliveryJson.Read<AccessRequest>("{\"serverPassword\":\"first\",\"serverPassword\":\"second\"}"u8));
    }

    [Fact]
    public void AboutMetadataUsesTheSharedHashAndRejectsExternalXml()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimlink-delivery-meta", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "About"));
        try
        {
            string path = Path.Combine(root, "About", "About.xml");
            File.WriteAllText(path, "<ModMetaData><packageId>Test.Mod</packageId><name>Test mod</name><modVersion>2</modVersion><supportedVersions><li>1.7</li></supportedVersions><modDependencies><li><packageId>brrainz.harmony</packageId></li></modDependencies></ModMetaData>");
            var mod = ModMetadata.Read(root, Guid.NewGuid().ToString("N"), 1);
            Assert.Equal("test.mod", mod.PackageId);
            Assert.Equal("brrainz.harmony", Assert.Single(mod.Dependencies));
            Assert.Equal(ModContent.Scan(root).FilesHash, mod.FilesHash);
            File.WriteAllText(path, "<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///not-read'>]><ModMetaData><packageId>&secret;</packageId></ModMetaData>");
            Assert.Throws<DeliveryException>(() => ModMetadata.Read(root, Guid.NewGuid().ToString("N"), 1));
        }
        finally { Directory.Delete(root, true); }
    }
}
