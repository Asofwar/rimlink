using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;
using Rimlink.Client.Outposts;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class GuildRecoveryMetadataTests
{
    [Fact]
    public void Guild_receipt_is_part_of_the_exact_XML_and_shared_envelope_without_personal_custody()
    {
        var metadata = Metadata();
        byte[] bytes = Xml(metadata);
        var decoded = SaveRecoveryData.ReadXml(new MemoryStream(bytes), out var hash);
        Assert.Equal(SHA256.HashData(bytes), hash);
        Assert.Equal(metadata, decoded.Outposts.Metadata);
        Assert.Empty(decoded.Outposts.Metadata.Registry); Assert.Empty(decoded.Outposts.Metadata.Receipts);
        Assert.Single(decoded.Outposts.Metadata.GuildMetadata.Receipts);
    }

    [Fact]
    public void Guild_only_download_requires_its_own_version_and_mixed_download_requires_both_families()
    {
        var metadata = Metadata(); metadata.GuildMetadata.Receipts.Clear();
        var recovery = new SaveRecoveryData { Version = 1, SnapshotId = Guid.NewGuid().ToString("N"), Outposts = new() { Metadata = metadata } };
        var header = new SaveHeader { SnapshotId = recovery.SnapshotId, RecoveryVersion = 1, GuildVersion = 1, OutpostMetadata = metadata.Clone() };
        var login = new LoginReply { Player = new() { PlayerId = 1 }, RecoveryVersion = 1, GuildVersion = 1, OutpostRestoreBoundary = metadata.Scope.RestoreBoundary };
        var hash = new byte[32];
        Assert.True(OutpostDownloadChecks.Validate(header, recovery, hash, login).Allowed);
        login.GuildVersion = 0;
        Assert.False(OutpostDownloadChecks.Validate(header, recovery, hash, login).Allowed);
        login.GuildVersion = 1;
        metadata.Registry.Add(new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = Guid.NewGuid().ToString("D"),
            PawnReferenceToken = "Thing_Human1", Scope = metadata.Scope.Clone() });
        header.OutpostMetadata = metadata.Clone();
        Assert.False(OutpostDownloadChecks.Validate(header, recovery, hash, login).Allowed);
        login.OutpostVersion = 1;
        Assert.True(OutpostDownloadChecks.Validate(header, recovery, hash, login).Allowed);
        header.GuildVersion = 0;
        Assert.False(OutpostDownloadChecks.Validate(header, recovery, hash, login).Allowed);
    }

    [Fact]
    public void An_empty_guild_bootstrap_has_real_scope_and_leaves_legacy_absence_absent()
    {
        var metadata = Metadata(); metadata.GuildMetadata.Receipts.Clear();
        new OutpostRecoveryData { Metadata = metadata }.Validate();
        metadata.GuildMetadata = null;
        byte[] original = metadata.ToByteArray();
        Assert.Equal(original, OutpostSaveMetadata.Parser.ParseFrom(original).ToByteArray());
        Assert.Null(OutpostSaveMetadata.Parser.ParseFrom(original).GuildMetadata);
    }

    [Theory]
    [InlineData("duplicate-parcel")][InlineData("duplicate-attempt")][InlineData("duplicate-claim")]
    [InlineData("hash")][InlineData("version")][InlineData("scope")][InlineData("boundary")][InlineData("future-epoch")]
    public void Invalid_or_foreign_guild_receipt_is_refused_before_save_acceptance(string change)
    {
        var metadata = Metadata(); var guild = metadata.GuildMetadata; var receipt = guild.Receipts[0];
        if (change.StartsWith("duplicate-", StringComparison.Ordinal))
        {
            var copy = receipt.Clone(); copy.AttemptId = Guid.NewGuid().ToString("D"); copy.ClaimId = Guid.NewGuid().ToString("D"); copy.ParcelId = 2;
            if (change == "duplicate-parcel") copy.ParcelId = receipt.ParcelId;
            if (change == "duplicate-attempt") copy.AttemptId = receipt.AttemptId;
            if (change == "duplicate-claim") copy.ClaimId = receipt.ClaimId;
            guild.Receipts.Add(copy);
        }
        if (change == "hash") receipt.AuthorizationTransitionDigest = ByteString.Empty;
        if (change == "version") guild.SchemaVersion = 2;
        if (change == "scope") guild.Scope.PlayerId = 2;
        if (change == "boundary") receipt.BeforeSnapshot.Scope.RestoreBoundary = Guid.NewGuid().ToString("D");
        if (change == "future-epoch") receipt.BeforeSnapshot.Scope.SaveWriteEpoch++;
        Assert.Throws<InvalidDataException>(() => new OutpostRecoveryData { Metadata = metadata }.Validate());
    }

    internal static OutpostSaveMetadata Metadata()
    {
        string boundary = Guid.NewGuid().ToString("D");
        var scope = new GuildColonyScope { WorldInstanceId = "world", PlayerId = 1, RestoreBoundary = boundary };
        var guild = new GuildSaveMetadata { SchemaVersion = 1, Scope = scope };
        guild.Receipts.Add(new GuildDeliveryReceipt { SchemaVersion = 1, AttemptId = Guid.NewGuid().ToString("D"), ClaimId = Guid.NewGuid().ToString("D"),
            ParcelId = 1, ClientRequestId = Guid.NewGuid().ToString("D"), PayloadDigest = ByteString.CopyFrom(new byte[32]),
            AuthorizationTransitionId = Guid.NewGuid().ToString("D"), AuthorizationTransitionDigest = ByteString.CopyFrom(new byte[32]),
            AuthorizationIntentDigest = ByteString.CopyFrom(new byte[32]), BeforeSnapshot = new() { SnapshotId = Guid.NewGuid().ToString("N"),
                RawSha256 = ByteString.CopyFrom(new byte[32]), Scope = scope.Clone() } });
        return new() { SchemaVersion = 1, Scope = new() { WorldInstanceId = "world", OwnerPlayerId = 1, RestoreBoundary = boundary }, GuildMetadata = guild };
    }

    private static byte[] Xml(OutpostSaveMetadata metadata)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            writer.WriteStartElement("savegame"); writer.WriteStartElement("game"); writer.WriteStartElement("components");
            writer.WriteStartElement("li"); writer.WriteAttributeString("Class", SaveRecoveryData.ComponentClass);
            writer.WriteElementString(SaveRecoveryData.VersionNode, "1"); writer.WriteElementString(SaveRecoveryData.SnapshotNode, Guid.NewGuid().ToString("N"));
            new OutpostRecoveryData { Metadata = metadata }.WriteSection(writer);
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        }
        return stream.ToArray();
    }
}
