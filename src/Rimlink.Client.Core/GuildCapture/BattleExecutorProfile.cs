using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public sealed record BattleRuntimeFacts(IReadOnlyList<BattleModInventoryEntry> Mods,IReadOnlyList<string> ActiveDlc,
    byte[] Config,byte[] LoadedDefs,string GameBuildPath,string RuntimeBuildPath,string ClientBuildPath,string ExecutorBuildPath)
{
    public static string NormalizeVersion(string? measured)=>string.IsNullOrWhiteSpace(measured)?"unknown":measured;
}

public sealed class BattleExecutorProfile
{
    private readonly BattleProfileV1 profile;
    private readonly BattleRuntimeInventoryV1 inventory;
    private readonly string? archive;
    private readonly BattleViewerCompatibilityV1? viewerCompatibility;
    public BattleProfileV1 Profile => profile.Clone();
    public BattleRuntimeInventoryV1 Inventory => inventory.Clone();
    public ByteString Digest { get; }
    private BattleExecutorProfile(BattleProfileV1 profile,BattleRuntimeInventoryV1 inventory,string? archive,byte[] digest,
        BattleViewerCompatibilityV1? viewerCompatibility = null)
    {
        this.profile=profile;this.inventory=inventory;this.archive=archive;Digest=ByteString.CopyFrom(digest);
        this.viewerCompatibility=viewerCompatibility;
    }

    public static BattleExecutorProfile Load(BattleExecutorSettings settings)
    {
        try
        {
            byte[] bytes=BattleExecutorSettings.ReadFile(settings.ProfilePath,BattleManifestContracts.ProfileBytes);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes))!=settings.ProfileSha256) throw Invalid();
            var profile=BattleManifestContracts.ReadProfile(bytes);
            byte[] digest=BattleManifestContracts.Digest(BattleDocumentKind.Profile,bytes);
            if (Convert.ToHexStringLower(digest)!=settings.ProfileDigest) throw Invalid();
            var inventory=BattleManifestContracts.ReadInventory(ReadReference(settings.ArchiveRoot,profile.Inventory));
            if (!profile.ExecutorBuildSha256.Equals(inventory.ExecutorBuildSha256)) throw Invalid();
            foreach(var descriptor in new[]{inventory.Config,inventory.LoadedDefs,profile.Template,profile.ViewerCompatibility})
                _=ReadReference(settings.ArchiveRoot,descriptor);
            return new(profile,inventory,settings.ArchiveRoot,digest);
        }
        catch(FileNotFoundException error) { throw new InvalidDataException("battle-runtime-profile-incomplete",error); }
        catch(DirectoryNotFoundException error) { throw new InvalidDataException("battle-runtime-profile-incomplete",error); }
    }
    public static BattleExecutorProfile LoadViewer(BattlePreparedEntry entry, ReadOnlySpan<byte> inventoryBytes)
    {
        var profile = BattleManifestContracts.ReadProfile(entry.Profile.Span);
        byte[] digest = BattleManifestContracts.Digest(BattleDocumentKind.Profile, entry.Profile.Span);
        if (!digest.AsSpan().SequenceEqual(entry.ProfileDigest.Span) || !Matches(inventoryBytes, profile.Inventory)
            || !Matches(entry.ViewerCompatibility.Span, profile.ViewerCompatibility)) throw Invalid();
        var inventory = BattleManifestContracts.ReadInventory(inventoryBytes);
        var compatibility = BattleManifestContracts.ReadViewerCompatibility(entry.ViewerCompatibility.Span);
        if (!profile.ExecutorBuildSha256.Equals(inventory.ExecutorBuildSha256)) throw Invalid();
        RequireViewerCompatibility(profile, compatibility);
        return new(profile, inventory, null, digest, compatibility);
    }

    public byte[] ReadTemplate() => ReadReference(archive ?? throw Invalid(),profile.Template);

    // A viewer never simulates: it loads the arbiter's snapshot and applies its deltas. What has to match is what decides
    // whether that snapshot loads the same way: the builds, the mods in order, their gameplay content and loaded definitions.
    // Where the files live, other files beside them and the player's own mod settings do not, so a Steam install qualifies.
    public void VerifyViewerRuntime(BattleRuntimeFacts actual)
    {
        var compatibility=viewerCompatibility ?? BattleManifestContracts.ReadViewerCompatibility(ReadReference(archive ?? throw Invalid(),profile.ViewerCompatibility));
        RequireViewerCompatibility(profile, compatibility);
        if (!inventory.Mods.SequenceEqual(actual.Mods) || !inventory.ActiveDlc.SequenceEqual(actual.ActiveDlc)
            || !Matches(actual.LoadedDefs,inventory.LoadedDefs)) throw Invalid();
        RequireHash(actual.GameBuildPath,inventory.GameBuildSha256);RequireHash(actual.RuntimeBuildPath,inventory.RuntimeBuildSha256);
        RequireHash(actual.ClientBuildPath,inventory.ClientBuildSha256);RequireHash(actual.ExecutorBuildPath,profile.ViewerBuildSha256);
    }
    private static void RequireHash(string path,ByteString expected)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(!SHA256.HashData(input).AsSpan().SequenceEqual(expected.Span))throw Invalid();
    }
    public void VerifyRuntime(IReadOnlyList<BattleRuntimeRootPath> roots,BattleRuntimeFacts actual)
    {
        if (!inventory.Mods.SequenceEqual(actual.Mods) || !inventory.ActiveDlc.SequenceEqual(actual.ActiveDlc)
            || !Matches(actual.Config,inventory.Config) || !Matches(actual.LoadedDefs,inventory.LoadedDefs)) throw Invalid();
        var mapped=new Dictionary<(BattleInventoryRoot,string),string>();
        foreach(var root in roots)
        {
            string path=Path.GetFullPath(root.Path);BattleExecutorSettings.RequireNoLinks(path);
            if (!Directory.Exists(path) || !mapped.TryAdd((root.Kind,root.Id),path)) throw Invalid();
        }
        var required=inventory.Files.Select(f=>(f.Root,f.RootId)).Concat(profile.ExcludedRuntimePaths.Select(f=>(f.Root,f.RootId)))
            .Concat(inventory.Mods.Select(m=>(BattleInventoryRoot.Mod,m.RootId))).ToHashSet();
        if (!required.SetEquals(mapped.Keys)) throw Invalid();
        // The inventory pins files by content, not directories by listing: every pinned file must be present unchanged
        // under its root, while other files beside it (logs, other mods, store files) are not this profile's business.
        foreach(var entry in inventory.Files)
        {
            string path=Path.Combine(mapped[(entry.Root,entry.RootId)],entry.RelativePath.Replace('/',Path.DirectorySeparatorChar));
            BattleExecutorSettings.RequireNoLinks(path);
            if (!File.Exists(path)) throw Invalid();
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if (input.Length!=entry.SizeBytes || !SHA256.HashData(input).AsSpan().SequenceEqual(entry.Sha256.Span)) throw Invalid();
        }
        RequireBuild(actual.GameBuildPath,inventory.GameBuildSha256,mapped);
        RequireBuild(actual.RuntimeBuildPath,inventory.RuntimeBuildSha256,mapped);
        RequireBuild(actual.ClientBuildPath,inventory.ClientBuildSha256,mapped);
        RequireBuild(actual.ExecutorBuildPath,inventory.ExecutorBuildSha256,mapped);
    }
    private void RequireBuild(string actualPath,ByteString hash,IReadOnlyDictionary<(BattleInventoryRoot,string),string> roots)
    {
        string full=Path.GetFullPath(actualPath);
        var comparer=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        if (!inventory.Files.Any(f=>f.Sha256.Equals(hash) && comparer.Equals(full,Path.GetFullPath(Path.Combine(roots[(f.Root,f.RootId)],f.RelativePath.Replace('/',Path.DirectorySeparatorChar)))))) throw Invalid();
    }
    private static byte[] ReadReference(string archive,BattleDocumentDescriptor descriptor)
    {
        BattleManifestContracts.RequireDescriptor(descriptor,descriptor.Kind);
        string path=Path.Combine(archive,"guild-profiles",((int)descriptor.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),Convert.ToHexStringLower(descriptor.Sha256.Span)+".artifact");
        byte[] bytes=BattleExecutorSettings.ReadFile(path,checked((int)descriptor.SizeBytes));
        if (!Matches(bytes,descriptor)) throw Invalid();return bytes;
    }
    private static void RequireViewerCompatibility(BattleProfileV1 profile, BattleViewerCompatibilityV1 compatibility)
    {
        if (!compatibility.Inventory.Equals(profile.Inventory) || !compatibility.ViewerBuildSha256.Equals(profile.ViewerBuildSha256)
            || compatibility.SnapshotEncodingVersion != profile.SnapshotEncodingVersion || compatibility.DeltaEncodingVersion != profile.DeltaEncodingVersion)
            throw Invalid();
    }
    private static bool Matches(ReadOnlySpan<byte> bytes,BattleDocumentDescriptor descriptor) => bytes.Length==descriptor.SizeBytes && SHA256.HashData(bytes).AsSpan().SequenceEqual(descriptor.Sha256.Span);
    private static InvalidDataException Invalid()=>new("battle-runtime-profile-or-inventory-mismatch");
}
