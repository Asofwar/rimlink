using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed record AcceptanceStand(int Version, string StandId, string Root, string DataDirectory, string AnchorDirectory,
    string BackupDirectory, string ArchiveDirectory, string GameRoot, string HostGameId, string GuestGameId,
    string HostSaveData, string GuestSaveData, string ModAssembly, string CompanionAssembly, string ConfigurationFile,
    string PrerequisitesFile, int Port, string QualifiedLinuxFileSystem);
internal sealed record AcceptanceOwner(int Version, string StandId, string LayoutDigest);
internal sealed record AcceptanceCheck(string Case, string Outcome, string SourceIdentity, string EvidenceFile, string EvidenceSha256);
internal sealed record AcceptanceBinary(string Path, string Assembly, string Sha256);
internal sealed record AcceptancePrerequisites(int Version, string SourceIdentity, AcceptanceCheck[] Checks, AcceptanceBinary[] Binaries);

internal static class AcceptanceStandFiles
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32, AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal static string OwnerPath(AcceptanceStand s) => Path.Combine(s.Root, "owner.json");
    internal static string LockPath(AcceptanceStand s) => Path.Combine(s.Root, "stand.lock");
    internal static T Read<T>(string path)
    {
        AcceptancePaths.File(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > MaximumBytes) throw new InvalidDataException("acceptance-document-size");
        try { return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("acceptance-document-invalid"); }
        catch (JsonException) { throw new InvalidDataException("acceptance-document-invalid"); }
    }
    internal static string HashFile(string path)
    {
        AcceptancePaths.File(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    internal static string LayoutDigest(AcceptanceStand stand) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stand, Json)));

    internal static void ValidateLayout(AcceptanceStand s)
    {
        if (s.Version != 1 || !Guid.TryParseExact(s.StandId, "D", out var id) || id == Guid.Empty || s.Port is < 1024 or > 65535
            || s.HostGameId != "rimlink-outposts-" + id.ToString("N") + "-host" || s.GuestGameId != "rimlink-outposts-" + id.ToString("N") + "-guest")
            throw new InvalidDataException("acceptance-stand-identity-invalid");
        string root = AcceptancePaths.Directory(s.Root);
        if (!root.Equals(Path.TrimEndingDirectorySeparator(s.Root), AcceptancePaths.Comparison)
            || root.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), AcceptancePaths.Comparison))
            throw new InvalidDataException("acceptance-root-invalid");
        var expected = new[] { (s.DataDirectory, "data"), (s.AnchorDirectory, "anchor"), (s.GameRoot, "game"),
            (s.HostSaveData, "saves-host"), (s.GuestSaveData, "saves-guest") };
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, name) in expected)
        {
            string canonical = AcceptancePaths.Directory(path);
            if (!canonical.Equals(Path.Combine(root, name), AcceptancePaths.Comparison) || !identities.Add(AcceptancePaths.DirectoryIdentity(canonical)))
                throw new InvalidDataException("acceptance-path-overlap-or-alias");
        }
        if (!AcceptancePaths.Directory(s.BackupDirectory).Equals(Path.Combine(s.DataDirectory, "backups"), AcceptancePaths.Comparison)
            || !AcceptancePaths.Directory(s.ArchiveDirectory).Equals(Path.Combine(s.DataDirectory, "archive"), AcceptancePaths.Comparison)
            || !Path.GetFullPath(s.ConfigurationFile).Equals(Path.Combine(root, "config.json"), AcceptancePaths.Comparison)
            || !Path.GetFullPath(s.PrerequisitesFile).Equals(Path.Combine(root, "prerequisites.json"), AcceptancePaths.Comparison)
            || !AcceptancePaths.Within(Path.GetFullPath(s.ModAssembly), Path.Combine(s.GameRoot, "Mods", "Rimlink"))
            || !AcceptancePaths.Within(Path.GetFullPath(s.CompanionAssembly), Path.Combine(s.GameRoot, "BridgeTools", "Rimlink")))
            throw new InvalidDataException("acceptance-path-layout-invalid");
        string[] allowed = ["data", "anchor", "game", "saves-host", "saves-guest", "evidence", "config.json", "prerequisites.json", "stand.json", "owner.json", "stand.lock"];
        if (Directory.EnumerateFileSystemEntries(root).Any(path => !allowed.Contains(Path.GetFileName(path), StringComparer.Ordinal)))
            throw new InvalidDataException("acceptance-foreign-root-state");
        OutpostStorageProfile.Validate(s.DataDirectory, s.AnchorDirectory, "sqlite-local-v1", s.QualifiedLinuxFileSystem);
    }

    internal static void PrepareOwner(AcceptanceStand s)
    {
        ValidateLayout(s);
        ValidateTree(s.GameRoot);
        foreach (string root in new[] { s.DataDirectory, s.AnchorDirectory, s.HostSaveData, s.GuestSaveData })
        {
            ValidateTree(root);
            if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any()) throw new InvalidDataException("acceptance-unowned-state-not-empty");
        }
        WriteNew(LockPath(s), Array.Empty<byte>());
        WriteNew(OwnerPath(s), JsonSerializer.SerializeToUtf8Bytes(new AcceptanceOwner(1, s.StandId, LayoutDigest(s)), Json));
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }
    internal static void VerifyOwner(AcceptanceStand s)
    {
        ValidateLayout(s);
        if (Read<AcceptanceOwner>(OwnerPath(s)) != new AcceptanceOwner(1, s.StandId, LayoutDigest(s)))
            throw new InvalidDataException("acceptance-owner-mismatch");
        AcceptancePaths.File(LockPath(s));
        foreach (string root in new[] { s.DataDirectory, s.AnchorDirectory, s.HostSaveData, s.GuestSaveData }) ValidateTree(root);
    }
    internal static void VerifyLaunchOwner(AcceptanceStand stand)
    { VerifyOwner(stand); ValidateTree(stand.GameRoot); }
    private static void ValidateTree(string directory)
    {
        var pending = new Stack<string>(); pending.Push(directory); int remaining = 100_000;
        while (pending.TryPop(out string? next))
            foreach (string path in Directory.EnumerateFileSystemEntries(next))
            {
                if (--remaining < 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("acceptance-tree-limit-or-alias");
                if (Directory.Exists(path)) pending.Push(path); else AcceptancePaths.File(path);
            }
    }

    internal static bool VerifyPrerequisites(AcceptanceStand stand)
    {
        var prerequisites = Read<AcceptancePrerequisites>(stand.PrerequisitesFile);
        string identity = OutpostProductQualification.SourceIdentity;
        if (prerequisites.Version != 1 || prerequisites.SourceIdentity != identity || prerequisites.Checks.Length != OutpostProductQualification.AutomatedCases.Length
            || prerequisites.Checks.Select(c => c.Case).Distinct(StringComparer.Ordinal).Count() != prerequisites.Checks.Length)
            throw new InvalidDataException("acceptance-prerequisites-incomplete");
        foreach (string name in OutpostProductQualification.AutomatedCases)
        {
            var check = prerequisites.Checks.SingleOrDefault(c => c.Case == name);
            if (check is null || check.Outcome != "PASS" || check.SourceIdentity != identity
                || !AcceptancePaths.Within(Path.GetFullPath(check.EvidenceFile), Path.Combine(stand.Root, "evidence"))
                || HashFile(check.EvidenceFile) != check.EvidenceSha256) throw new InvalidDataException("acceptance-prerequisite-unverified:" + name);
        }
        var expected = new[] { (stand.ModAssembly, "Rimlink.Client"), (stand.CompanionAssembly, "Rimlink.BridgeTools"),
            (Path.Combine(Path.GetDirectoryName(stand.ModAssembly)!, "Rimlink.Client.Core.dll"), "Rimlink.Client.Core"),
            (Path.Combine(Path.GetDirectoryName(stand.ModAssembly)!, "Rimlink.Protocol.dll"), "Rimlink.Protocol") };
        if (prerequisites.Binaries.Length != expected.Length) throw new InvalidDataException("acceptance-binary-inventory-invalid");
        foreach (var (path, name) in expected)
        {
            var binary = prerequisites.Binaries.SingleOrDefault(b => b.Assembly == name && b.Path.Equals(path, AcceptancePaths.Comparison));
            if (binary is null || HashFile(path) != binary.Sha256 || AssemblyName.GetAssemblyName(path).Name != name)
                throw new InvalidDataException("acceptance-binary-identity-invalid");
        }
        return true;
    }
}
