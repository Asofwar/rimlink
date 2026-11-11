using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.GuildAcceptanceHost;

internal sealed record GuildStandFile(string Name,string RelativePath,string Sha256);
internal sealed record GuildStandCheck(string Case,string SourceIdentity,string Outcome,string RelativePath,string Sha256);
internal sealed record GuildStandRequest(int Version,string StandId,string Root,string SourceRoot,int Port,string ExecutorId,string ProfileDigest,
    string AttackerLogin,string DefenderLogin,GuildStandFile[] Binaries,GuildStandCheck[] Prerequisites,string[] ExcludedRoots);
internal sealed record GuildStandBootstrap(int Version,string Mode,string OwnerId,string SourceIdentity,int DomainFormat,GuildStandRequest Stand,string ConfigurationSha256,GuildStandClockOrigin Clock);
internal sealed record GuildStandWorld(int Version,string StandId,string BootstrapSha256,string WorldId,string Boundary);
internal sealed record GuildStandSlice(int Version,string StandId,string BootstrapSha256,string WorldSha256,GuildStandCheck[] Prerequisites);

internal static class GuildStandFiles
{
    internal const string Mode="GuildCaptureIsolatedAcceptanceV1";
    internal static readonly string[] HomeCases=["v1-v2-history","v3-authority","session-role-artifact-dispatch","battle-context-home-canonical","shipping-builds","profile-reference-closure"];
    internal static readonly string[] SliceCases=["native-home","capture-claim-receipt-wealth-negatives"];
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){MaxDepth=32,AllowDuplicateProperties=false,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectNullableAnnotations=true,RespectRequiredConstructorParameters=true};
    internal static void RelativeFile(string value)
    {
        if(string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.Contains(':') || value.StartsWith('/')
            || value.Split('/').Any(p=>p is "" or "." or ".."))throw new InvalidDataException("guild-stand-relative-file-invalid");
    }
    internal static string PathIn(GuildStandRequest stand,string relative)
    {RelativeFile(relative);return Path.Combine(stand.Root,relative.Replace('/',Path.DirectorySeparatorChar));}
    internal static string Hash(string file)
    {AcceptancePaths.File(file);using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexStringLower(SHA256.HashData(stream));}
    internal static T Read<T>(string file)
    {
        AcceptancePaths.File(file);using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length is <1 or >4*1024*1024)throw new InvalidDataException("guild-stand-document-size");
        return JsonSerializer.Deserialize<T>(stream,Json)??throw new InvalidDataException("guild-stand-document-invalid");
    }
    internal static void Create<T>(string path,T value)
    {
        _=AcceptancePaths.Directory(Path.GetDirectoryName(path)!);
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(value,Json));stream.Flush(true);
    }
    internal static void ValidateRequest(GuildStandRequest stand)
    {
        if(stand.Version!=1 || !Guid.TryParseExact(stand.StandId,"D",out var id) || id==Guid.Empty || id.ToString("D")!=stand.StandId
            || stand.Port is <1024 or >65535 || !Identifier(stand.ExecutorId) || !HashText(stand.ProfileDigest)
            || !Identifier(stand.AttackerLogin) || !Identifier(stand.DefenderLogin) || stand.AttackerLogin==stand.DefenderLogin)
            throw new InvalidDataException("guild-stand-identity-invalid");
    }
    internal static void ValidateLayout(GuildStandRequest stand,bool staged)
    {
        ValidateRequest(stand);string root=AcceptancePaths.Directory(stand.Root),source=AcceptancePaths.Directory(stand.SourceRoot);
        if(root!=stand.Root || source!=stand.SourceRoot || AcceptancePaths.Within(root,source) || AcceptancePaths.Within(source,root))throw new InvalidDataException("guild-stand-source-overlap");
        var roots=new List<string>{source};
        foreach(string excluded in stand.ExcludedRoots)roots.Add(AcceptancePaths.Directory(excluded));
        foreach(string other in roots)
            if(AcceptancePaths.Within(root,other) || AcceptancePaths.Within(other,root))throw new InvalidDataException("guild-stand-excluded-root-overlap");
        var identities=new HashSet<string>(StringComparer.Ordinal);
        foreach(string name in new[]{"game","saves-executor","saves-attacker","saves-defender","evidence"}.Concat(staged?[]:new[]{"data","anchor"}))
        {
            string path=PathIn(stand,name);if(AcceptancePaths.Directory(path)!=path || !identities.Add(AcceptancePaths.DirectoryIdentity(path)))throw new InvalidDataException("guild-stand-path-alias");
            ValidateTree(path);
        }
        if(staged && (Directory.Exists(PathIn(stand,"data")) || Directory.Exists(PathIn(stand,"anchor")) || File.Exists(PathIn(stand,"bootstrap.json"))))throw new InvalidDataException("guild-stand-must-be-new");
        AcceptancePaths.File(PathIn(stand,OperatingSystem.IsWindows()?"game/RimWorld.exe":"game/RimWorldLinux"));
    }
    internal static void ValidateTree(string directory)
    {
        var pending=new Stack<string>();pending.Push(directory);int remaining=150_000;
        while(pending.TryPop(out string? next))foreach(string path in Directory.EnumerateFileSystemEntries(next))
        {
            if(--remaining<0 || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("guild-stand-tree-alias-or-limit");
            if(Directory.Exists(path))pending.Push(path);else AcceptancePaths.File(path);
        }
    }
    internal static bool InitialSaveDataFile(string relative)
    {
        if(relative is "Config/ModsConfig.xml" or "Config/Prefs.xml" or "Config/KeyPrefs.xml" or "Config/Knowledge.xml" or "Config/LastPlayedVersion.txt" or "HardwareProfile/gpu-profile.bin")return true;
        if(relative.StartsWith("ShaderCache/",StringComparison.Ordinal)&&relative.EndsWith(".layout-cache",StringComparison.Ordinal))return true;
        if(relative.StartsWith("TextureCache/v",StringComparison.Ordinal)&&(relative.EndsWith(".zip",StringComparison.Ordinal)||relative.EndsWith(".zip.lock",StringComparison.Ordinal)))return true;
        return relative.StartsWith("TextureCache/installs/",StringComparison.Ordinal)&&relative["TextureCache/installs/".Length..] is{Length:16} name&&name.All(Uri.IsHexDigit);
    }
    internal static void Checks(GuildStandRequest stand,IReadOnlyList<GuildStandCheck> checks,IReadOnlyList<string> required,string source)
    {
        if(checks.Count!=required.Count || checks.Select(c=>c.Case).Distinct(StringComparer.Ordinal).Count()!=required.Count)throw new InvalidDataException("guild-stand-prerequisites-incomplete");
        foreach(string name in required)
        {
            var check=checks.SingleOrDefault(c=>c.Case==name);
            if(check is null || check.SourceIdentity!=source || check.Outcome!="PASS" || !check.RelativePath.StartsWith("evidence/",StringComparison.Ordinal)
                || Hash(PathIn(stand,check.RelativePath))!=check.Sha256)throw new InvalidDataException("guild-stand-prerequisite-unverified:"+name);
        }
    }
    internal static void Binaries(GuildStandRequest stand)
    {
        string[] names=["rimlink-server","Rimlink.GuildAcceptanceHost","Rimlink.Client.Core","Rimlink.Client","Rimlink.Protocol","Rimlink.BridgeTools"];
        if(stand.Binaries.Length!=names.Length || stand.Binaries.Select(b=>b.Name).Distinct().Count()!=names.Length)throw new InvalidDataException("guild-stand-binary-inventory");
        foreach(string name in names)
        {
            var entry=stand.Binaries.SingleOrDefault(b=>b.Name==name)??throw new InvalidDataException("guild-stand-binary-missing");
            string path=PathIn(stand,entry.RelativePath);
            if(Hash(path)!=entry.Sha256 || AssemblyName.GetAssemblyName(path).Name!=name)throw new InvalidDataException("guild-stand-binary-mismatch");
            string? loaded=name switch{"rimlink-server"=>typeof(OutpostFeature).Assembly.Location,"Rimlink.GuildAcceptanceHost"=>typeof(GuildStandFiles).Assembly.Location,_=>null};
            if(loaded is not null && Hash(loaded)!=entry.Sha256)throw new InvalidDataException("guild-stand-loaded-binary-mismatch");
        }
    }
    internal static bool HashText(string value)=>value is{Length:64}&&value.All(c=>c is >='0' and <='9' or >='a' and <='f');
    private static bool Identifier(string value)=>value is{Length:>0 and <=128}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.');
}
