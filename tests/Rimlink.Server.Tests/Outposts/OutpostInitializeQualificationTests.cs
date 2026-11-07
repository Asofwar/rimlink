using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Rimlink.Server.Application.Outposts;
using WorldEntity = Rimlink.Server.Storage.Entities.World;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostInitializeQualificationTests
{
    [Fact]
    public void Stable_qualification_is_length_delimited_and_domain_separated()
    {
        var f = Facts(); var bytes = OutpostInitializeQualification.Encode(f);
        Assert.Equal(bytes, OutpostInitializeQualification.Encode(f));
        Assert.Equal("Rimlink.Outposts.InitializeQualification/v1", FirstText(bytes));
        Assert.Equal(SHA256.HashData(bytes), OutpostInitializeQualification.Digest(f));
        Assert.Equal("E7863B49A15CBF6FA6DC6DEBAA13986176D08E0A24EF78C03FE381EF6A9CDAF8", Convert.ToHexString(OutpostInitializeQualification.Digest(f)));
        Assert.NotEqual(OutpostInitializeQualification.WorldDigest(World(), "world-1"), OutpostInitializeQualification.Digest(f));
        // The entire command separately binds the logical observation and preallocated ids; they are not physical identities.
        Assert.False(Encoding.UTF8.GetString(bytes).Contains("Pending", StringComparison.Ordinal));
        Assert.False(Encoding.UTF8.GetString(bytes).Contains("NULL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("data")][InlineData("main")][InlineData("anchor")][InlineData("authority")]
    [InlineData("data-mount")][InlineData("anchor-mount")][InlineData("data-fs")][InlineData("anchor-fs")]
    [InlineData("native-version")][InlineData("source-id")][InlineData("vfs")][InlineData("native-hash")]
    [InlineData("managed-id")][InlineData("managed-version")][InlineData("pcl-id")][InlineData("pcl-version")]
    [InlineData("role")][InlineData("schema")]
    public void Every_stable_qualification_fact_is_committed_without_host_normalization(string field)
    {
        var f = Facts(); var b = f.AnchorBackend; var baseline = OutpostInitializeQualification.Digest(f);
        var changed = field switch
        {
            "data" => f with { DataDirectory = "/DATA" },
            "main" => f with { MainDatabase = "/data/other.db" },
            "anchor" => f with { AnchorDirectory = "/ANCHOR" },
            "authority" => f with { AuthorityDatabase = "/anchor/other.db" },
            "data-mount" => f with { DataFileSystem = f.DataFileSystem with { Mount = "/other" } },
            "anchor-mount" => f with { AnchorFileSystem = f.AnchorFileSystem with { Mount = "/other" } },
            "data-fs" => f with { DataFileSystem = f.DataFileSystem with { Kind = "xfs" } },
            "anchor-fs" => f with { AnchorFileSystem = f.AnchorFileSystem with { Kind = "xfs" } },
            "native-version" => f with { AnchorBackend = b with { NativeVersion = "changed" } },
            "source-id" => f with { AnchorBackend = b with { SourceId = "changed" } },
            "vfs" => f with { AnchorBackend = b with { Vfs = "changed" } },
            "native-hash" => f with { AnchorBackend = b with { NativeSha256 = SHA256.HashData("other"u8) } },
            "managed-id" => f with { AnchorBackend = b with { ManagedProviderIdentity = "changed" } },
            "managed-version" => f with { AnchorBackend = b with { ManagedProviderVersion = "changed" } },
            "pcl-id" => f with { AnchorBackend = b with { PclProviderIdentity = "changed" } },
            "pcl-version" => f with { AnchorBackend = b with { PclProviderVersion = "changed" } },
            "role" => f with { AnchorBackend = f.DomainBackend, DomainBackend = b },
            "schema" => f with { Schema = new("rimlink-outposts-schema/v2-lf-lf", Convert.FromHexString("b6bea3816a1adc5963ca0f1e63c833474d5b92081d79a160d1e4b78db6f8cbd2")) },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.NotEqual(baseline, OutpostInitializeQualification.Digest(changed));
        Assert.Equal(baseline, OutpostInitializeQualification.Digest(f));
    }

    [Theory]
    [InlineData("contract")][InlineData("revision")][InlineData("layout")][InlineData("appid")]
    [InlineData("user-version")][InlineData("singleton")][InlineData("path-size")][InlineData("invalid-utf8")]
    [InlineData("native-hash")][InlineData("schema")]
    public void Unsupported_or_unbounded_qualification_is_refused(string field)
    {
        var f = Facts();
        var bad = field switch
        {
            "contract" => f with { Contract = "guessed" },
            "revision" => f with { AdmissionRevision = 0 },
            "layout" => f with { AnchorLayoutVersion = 2 },
            "appid" => f with { AnchorApplicationId = 0 },
            "user-version" => f with { AnchorUserVersion = 2 },
            "singleton" => f with { AnchorSingletonId = 2 },
            "path-size" => f with { MainDatabase = new string('a', 4097) },
            "invalid-utf8" => f with { MainDatabase = "\ud800" },
            "native-hash" => f with { DomainBackend = f.DomainBackend with { NativeSha256 = new byte[31] } },
            "schema" => f with { Schema = new(f.Schema.ProfileId, new byte[32]) },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.Throws<InvalidDataException>(() => OutpostInitializeQualification.Encode(bad));
    }

    [Theory]
    [InlineData("id")][InlineData("seed")][InlineData("params")][InlineData("npc")]
    [InlineData("created")][InlineData("instance")][InlineData("revision")]
    public void Full_world_projection_is_bound_without_reserializing_parameters_or_converting_ticks(string field)
    {
        var original = World(); var baseline = OutpostInitializeQualification.WorldDigest(original, original.InfluenceInstanceId);
        var w = World();
        switch (field)
        {
            case "id": w.Id++; break;
            case "seed": w.Seed = "Seed"; break;
            case "params": w.Params = [1, 2, 4]; break;
            case "npc": w.NpcTiles = "1,2\r\n"; break;
            case "created": w.CreatedAt = w.CreatedAt.AddTicks(1); break;
            case "instance": w.InfluenceInstanceId = "world-2"; break;
            case "revision": w.InfluenceRevision++; break;
        }
        Assert.NotEqual(baseline, OutpostInitializeQualification.WorldDigest(w, w.InfluenceInstanceId));
        Assert.Equal(baseline, OutpostInitializeQualification.WorldDigest(original, original.InfluenceInstanceId));
    }

    [Fact]
    public void Stored_ticks_and_arbitrary_parameter_bytes_are_not_normalized()
    {
        var world = World(); var bytes = OutpostInitializeQualification.EncodeWorld(world, world.InfluenceInstanceId);
        Assert.Equal("Rimlink.Outposts.InitializeWorldBinding/v1", FirstText(bytes));
        var digest = SHA256.HashData(bytes); Assert.Equal(digest, OutpostInitializeQualification.WorldDigest(world, "world-1"));
        Assert.Equal("A5C5526E3FEBFF418BE1A50D85ADA8E4F90EA8EEC4CCE52B47FD4F7E7DD43FCD", Convert.ToHexString(digest));
        world.CreatedAt = DateTime.SpecifyKind(world.CreatedAt, DateTimeKind.Unspecified);
        Assert.Equal(digest, OutpostInitializeQualification.WorldDigest(world, "world-1"));
        Assert.Equal(new byte[] { 1, 2, 3 }, world.Params);
    }

    [Fact]
    public void Missing_world_projection_is_not_an_empty_world_identity()
    {
        var w = World(); w.Params = [];
        Assert.Throws<InvalidDataException>(() => OutpostInitializeQualification.WorldDigest(w, "world-1"));
        w = World(); w.InfluenceRevision = -1;
        Assert.Throws<InvalidDataException>(() => OutpostInitializeQualification.WorldDigest(w, "world-1"));
        Assert.Throws<InvalidDataException>(() => OutpostInitializeQualification.WorldDigest(World(), "other"));
    }

    private static string FirstText(byte[] bytes) => Encoding.UTF8.GetString(bytes.AsSpan(4, BinaryPrimitives.ReadInt32LittleEndian(bytes)));
    internal static WorldEntity World() => new() { Id = 1, Seed = "seed", Params = [1, 2, 3], NpcTiles = "1,2", CreatedAt = new(638900000000000000, DateTimeKind.Utc), InfluenceInstanceId = "world-1", InfluenceRevision = 3 };
    internal static OutpostInitializeStorageFacts Facts()
    {
        var anchor = new OutpostInitializeBackendFacts("3.53.3", "source", "win32", SHA256.HashData("anchor-native"u8), "Microsoft.Data.Sqlite", "10.0.14.0", "e_sqlite3", "2.1.13.3146");
        var domain = anchor with { NativeSha256 = SHA256.HashData("domain-native"u8) };
        return new("sqlite-local-v1", 1, "/data", "/data/rimlink.db", "/anchor", "/anchor/authority.db",
            new("/", "ext4"), new("/", "ext4"), 1380732737, 1, 1, 1, anchor, domain,
            new("rimlink-outposts-schema/v2-crlf-crlf", Convert.FromHexString("73ef95ae47f0bd493ce2ea2d8ab1e1e4b1c8d4fa25ca2b3fa0d746f4732c374a")));
    }
}
