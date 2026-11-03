using System.Security.Cryptography;
using System.Text;
using Rimlink.Protocol;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public sealed class SaveRecoveryDataTests
{
    private const string Id = "00112233445566778899aabbccddeeff";
    private static string Xml(string fields, string suffix = "") => $"<savegame><game><components><li Class=\"Rimlink.Client.Game.RimlinkGameComponent\">{fields}</li></components></game></savegame>{suffix}";
    private static SaveRecoveryData Read(string xml) => SaveRecoveryData.ReadXml(new MemoryStream(Encoding.UTF8.GetBytes(xml)), out _);

    [Fact]
    public void ReceiptsBelongToOneSnapshotAndHashCoversTheWholeFile()
    {
        string xml = Xml($"<rimlinkRecoveryVersion>1</rimlinkRecoveryVersion><rimlinkSnapshotId>{Id}</rimlinkSnapshotId><rimlinkSaveGeneration>2</rimlinkSaveGeneration><rimlinkSaveWriteEpoch>3</rimlinkSaveWriteEpoch><rimlinkParcelReceipts><li>7</li><li>9</li></rimlinkParcelReceipts><rimlinkRemovedOperations><li>{Id}</li></rimlinkRemovedOperations>");
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        var data = SaveRecoveryData.ReadXml(new MemoryStream(bytes), out byte[] rawHash);
        Assert.Equal(1u, data.Version); Assert.Equal(Id, data.SnapshotId);
        Assert.Equal(2, data.Generation); Assert.Equal(3, data.WriteEpoch);
        Assert.Equal(new long[] { 7, 9 }, data.ParcelIds); Assert.Equal(new[] { Id }, data.Removed);
        Assert.Equal(SHA256.HashData(bytes), rawHash);
    }

    [Theory]
    [InlineData("<rimlinkRecoveryVersion>1</rimlinkRecoveryVersion>")]
    [InlineData("<rimlinkRecoveryVersion>2</rimlinkRecoveryVersion>")]
    [InlineData("<rimlinkRecoveryVersion>1</rimlinkRecoveryVersion><rimlinkSnapshotId>bad</rimlinkSnapshotId>")]
    public void IncompleteOrUnknownRecoveryIsNotLegacy(string fields) => Assert.Throws<InvalidDataException>(() => Read(Xml(fields)));

    [Fact]
    public void DuplicateReceiptsAndBrokenTailAreRejected()
    {
        string fields = $"<rimlinkRecoveryVersion>1</rimlinkRecoveryVersion><rimlinkSnapshotId>{Id}</rimlinkSnapshotId>";
        Assert.Throws<InvalidDataException>(() => Read(Xml(fields + "<rimlinkParcelReceipts><li>7</li><li>7</li></rimlinkParcelReceipts>")));
        Assert.ThrowsAny<Exception>(() => Read(Xml(fields, "<broken>")));
    }

    [Fact]
    public void AnotherModsSameNamedFieldsAreNotRecoveryMetadata()
    {
        string xml = Xml("<rimlinkRecoveryVersion>99</rimlinkRecoveryVersion>").Replace("Rimlink.Client.Game.RimlinkGameComponent", "Other.Mod.Component");
        Assert.Equal(0u, Read(xml).Version);
    }

    [Fact]
    public void OldComponentWithoutRecoveryFieldsIsLegacy() => Assert.Equal(0u, Read(Xml("<rimlinkSaveGeneration>4</rimlinkSaveGeneration>")).Version);
}
