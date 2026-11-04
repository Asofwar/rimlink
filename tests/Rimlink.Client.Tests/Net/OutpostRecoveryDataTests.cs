using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Net;

// Removing format, uniqueness, or digest validation must allow each rejected fixture below.
public sealed class OutpostRecoveryDataTests
{
    private const string Snapshot = "00112233445566778899aabbccddeeff";
    private const string Custody = "11111111-1111-1111-1111-111111111111";
    private const string Assignment = "22222222-2222-2222-2222-222222222222";
    private const string Transition = "33333333-3333-3333-3333-333333333333";
    private const string Request = "44444444444444444444444444444444";
    private static readonly ByteString Digest = ByteString.CopyFrom(new byte[32]);
    private static OutpostScopeMessage Scope() => new() { WorldInstanceId = "world-1", OwnerPlayerId = 7, SaveGeneration = 2, SaveWriteEpoch = 3, RestoreBoundary = "boundary-1" };
    private static OutpostSaveMetadata Metadata()
    {
        var metadata = new OutpostSaveMetadata { SchemaVersion = 1, Scope = Scope() };
        metadata.Registry.Add(new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = Custody, AssignmentId = Assignment, Scope = Scope(), PawnReferenceToken = "Thing_Human42" });
        metadata.Receipts.Add(new OutpostTransitionReceipt
        {
            TransitionId = Transition, TransitionDigest = Digest, ClientRequestId = Request, IntentDigest = Digest,
            Snapshot = new OutpostSnapshotProof { SnapshotId = Snapshot, RawSha256 = Digest, Scope = Scope() },
            PawnCustodyIds = { Custody }, AssignmentIds = { Assignment }
        });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = Transition, TransitionDigest = Digest });
        return metadata;
    }
    private static string Section(OutpostSaveMetadata metadata) => "<rimlinkOutposts><version>1</version><metadata>" + Convert.ToBase64String(metadata.ToByteArray()) + "</metadata></rimlinkOutposts>";
    private static string Xml(string section, string component = SaveRecoveryData.ComponentClass) => $"<savegame><game><components><li Class='{component}'><rimlinkRecoveryVersion>1</rimlinkRecoveryVersion><rimlinkSnapshotId>{Snapshot}</rimlinkSnapshotId><rimlinkSaveGeneration>2</rimlinkSaveGeneration><rimlinkSaveWriteEpoch>3</rimlinkSaveWriteEpoch>{section}<rimlinkParcelReceipts><li>9</li></rimlinkParcelReceipts></li></components></game></savegame>";
    private static SaveRecoveryData Read(string xml) => SaveRecoveryData.ReadXml(new MemoryStream(Encoding.UTF8.GetBytes(xml)), out _);

    [Fact]
    public void Versioned_section_preserves_legacy_receipts_and_hashes_the_whole_XML()
    {
        var metadata = Metadata();
        string xml = Xml(Section(metadata));
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(Encoding.UTF8.GetBytes(xml)), out var rawHash);
        Assert.Equal(1u, recovery.Version); Assert.Equal(new long[] { 9 }, recovery.ParcelIds);
        Assert.True(recovery.Outposts.Present); Assert.Equal(metadata, recovery.Outposts.Metadata);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(xml)), rawHash);
    }
    [Fact]
    public void Missing_section_is_legacy_but_other_mods_section_is_ignored()
    {
        Assert.False(Read(Xml("")).Outposts.Present);
        Assert.False(Read(Xml(Section(Metadata()), "Other.Mod.Component")).Outposts.Present);
    }
    [Theory]
    [InlineData("<rimlinkOutposts/>")]
    [InlineData("<rimlinkOutposts><version>2</version><metadata>CAI=</metadata></rimlinkOutposts>")]
    [InlineData("<rimlinkOutposts><version>1</version><version>1</version><metadata>CAE=</metadata></rimlinkOutposts>")]
    [InlineData("<rimlinkOutposts><version>1</version><metadata>@@</metadata></rimlinkOutposts>")]
    [InlineData("<rimlinkOutposts><version>1</version><metadata><nested/></metadata></rimlinkOutposts>")]
    [InlineData("<rimlinkOutposts><version>1</version><unexpected/></rimlinkOutposts>")]
    [InlineData("<rimlinkOutposts extra='1'><version>1</version><metadata>CAE=</metadata></rimlinkOutposts>")]
    public void Unknown_version_is_not_empty(string section) => Assert.ThrowsAny<Exception>(() => Read(Xml(section)));
    [Fact]
    public void Duplicate_section_is_rejected() => Assert.Throws<InvalidDataException>(() => Read(Xml(Section(Metadata()) + Section(Metadata()))));
    [Theory]
    [InlineData("schema")][InlineData("scope")][InlineData("registry-schema")][InlineData("custody")]
    [InlineData("duplicate-custody")][InlineData("duplicate-reference")][InlineData("duplicate-receipt")]
    [InlineData("duplicate-frontier")][InlineData("digest")][InlineData("intent-digest")][InlineData("snapshot-hash")]
    [InlineData("enum")][InlineData("predecessor")][InlineData("duplicate-parcel")][InlineData("frontier-digest")]
    public void Invalid_ids_enums_digests_and_ambiguities_are_rejected(string mutation)
    {
        var metadata = Metadata();
        switch (mutation)
        {
            case "schema": metadata.SchemaVersion = 99; break;
            case "scope": metadata.Scope.OwnerPlayerId = 0; break;
            case "registry-schema": metadata.Registry[0].SchemaVersion = 99; break;
            case "custody": metadata.Registry[0].PawnCustodyId = "Thing_Human42"; break;
            case "duplicate-custody": metadata.Registry.Add(metadata.Registry[0].Clone()); break;
            case "duplicate-reference": var duplicate = metadata.Registry[0].Clone(); duplicate.PawnCustodyId = "55555555-5555-5555-5555-555555555555"; metadata.Registry.Add(duplicate); break;
            case "duplicate-receipt": metadata.Receipts.Add(metadata.Receipts[0].Clone()); break;
            case "duplicate-frontier": metadata.Frontier.Add(metadata.Frontier[0].Clone()); break;
            case "digest": metadata.Receipts[0].TransitionDigest = ByteString.Empty; break;
            case "intent-digest": metadata.Receipts[0].IntentDigest = ByteString.Empty; break;
            case "snapshot-hash": metadata.Receipts[0].Snapshot.RawSha256 = ByteString.CopyFrom(new byte[31]); break;
            case "enum": metadata.Receipts[0].CancellationKind = (OutpostCancelKind)99; break;
            case "predecessor": metadata.Frontier[0].PredecessorId = "bad"; break;
            case "duplicate-parcel": metadata.Receipts[0].ParcelIds.Add(new long[] { 7, 7 }); break;
            case "frontier-digest": metadata.Frontier[0].TransitionDigest = ByteString.Empty; break;
        }
        Assert.Throws<InvalidDataException>(() => Read(Xml(Section(metadata))));
    }
    [Theory]
    [InlineData("worker-digest")][InlineData("worker-custody")][InlineData("worker-duplicate")]
    [InlineData("caravan-branch")][InlineData("caravan-duplicate")][InlineData("caravan-request")]
    [InlineData("caravan-roster")][InlineData("cancel-branch")]
    public void Admission_attestation_shape_is_bounded_and_unambiguous(string fault)
    {
        var metadata = Metadata();
        var worker = new OutpostWorkerCapture { PawnCustodyId = Custody, AssignmentId = Assignment,
            Pawn = new PawnBlob { Registry = metadata.Registry[0].Clone(), ScribeXmlZstd = ByteString.CopyFromUtf8("blob") } };
        worker.BlobSha256 = ByteString.CopyFrom(SHA256.HashData(worker.Pawn.ToByteArray())); metadata.WorkerCaptures.Add(worker);
        var caravan = new OutpostCaravanAttestation { CaravanId = "Caravan_1", SurfaceId = "planet", GameBranchId = Transition,
            ClientRequestId = Request, ManifestDigest = Digest, RosterReferenceTokens = { "Thing_Human42" }, SelectedCustodyIds = { Custody } };
        metadata.Caravans.Add(caravan);
        switch (fault)
        {
            case "worker-digest": worker.BlobSha256 = ByteString.Empty; break;
            case "worker-custody": worker.PawnCustodyId = "bad"; break;
            case "worker-duplicate": metadata.WorkerCaptures.Add(worker.Clone()); break;
            case "caravan-branch": caravan.GameBranchId = "bad"; break;
            case "caravan-duplicate": metadata.Caravans.Add(caravan.Clone()); break;
            case "caravan-request": caravan.ClientRequestId = "bad"; break;
            case "caravan-roster": caravan.RosterReferenceTokens.Add("Thing_Human42"); break;
            case "cancel-branch": metadata.Receipts[0].NoRemovalBranchId = "bad"; break;
        }
        Assert.Throws<InvalidDataException>(() => Read(Xml(Section(metadata))));
    }
    [Fact]
    public void Excessive_counts_and_bytes_are_refused_before_publication()
    {
        var metadata = Metadata(); metadata.Registry.Clear();
        for (int i = 0; i < 10_001; i++) metadata.Registry.Add(new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = Guid.NewGuid().ToString("D"), Scope = Scope(), PawnReferenceToken = "Pawn" + i });
        Assert.Throws<InvalidDataException>(() => Read(Xml(Section(metadata))));
        string oversized = "<rimlinkOutposts><version>1</version><metadata>" + new string('A', 5_592_412) + "</metadata></rimlinkOutposts>";
        Assert.Throws<InvalidDataException>(() => Read(Xml(oversized)));
    }
    [Fact]
    public void Legacy_recovery_JSON_round_trip_preserves_exact_outpost_metadata()
    {
        var original = Read(Xml(Section(Metadata())));
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<SaveRecoveryData>(System.Text.Json.JsonSerializer.Serialize(original))!;
        roundTrip.Validate();
        Assert.Equal(original.Outposts.Metadata, roundTrip.Outposts.Metadata);
    }
    [Fact]
    public void Bootstrap_predecessor_and_exact_cancellation_markers_round_trip()
    {
        var metadata = Metadata(); metadata.Receipts[0].CancellationKind = OutpostCancelKind.ReturnAfterRemoval;
        var data = new OutpostRecoveryData { Metadata = metadata };
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true })) data.WriteSection(writer);
        var parsed = Read(Xml(output.ToString()));
        Assert.Equal(OutpostCancelKind.ReturnAfterRemoval, parsed.Outposts.Metadata.Receipts[0].CancellationKind);
        Assert.Empty(parsed.Outposts.Metadata.Frontier[0].PredecessorId);
        Assert.Equal(metadata, parsed.Outposts.Metadata);
    }
}
