using System.Security.Cryptography;
using System.Text;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.GuildAcceptanceHost;

// Read-sharing pins the verified bytes for this run; changes require a new source-bound bootstrap.
internal sealed class GuildStandEvidence:IDisposable
{
    private readonly string sourceRoot;
    private readonly List<(string Path,FileStream Stream,long Length,DateTime Written)> files=[];
    private readonly Dictionary<string,DateTime> directories=new(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
    internal GuildStandEvidence(GuildStandBootstrap bootstrap)
    {
        sourceRoot=bootstrap.Stand.SourceRoot;
        try
        {
            var stand=bootstrap.Stand;
            using var resource=typeof(OutpostFeature).Assembly.GetManifestResourceStream("Rimlink.Outposts.SafetySources")??throw new InvalidDataException("guild-source-manifest-missing");
            using var reader=new StreamReader(resource,Encoding.UTF8,true);
            var entries=reader.ReadToEnd().Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Split('|')).ToArray();
            var expected=entries.Select(e=>e[0]).Order(StringComparer.Ordinal).ToArray();
            if(!expected.SequenceEqual(RelevantFiles(stand.SourceRoot).Order(StringComparer.Ordinal)))throw new InvalidDataException("guild-source-inventory-drift");
            foreach(var entry in entries)Pin(Path.Combine(stand.SourceRoot,entry[0]),entry[1].ToLowerInvariant());
            Pin(GuildStandFiles.PathIn(stand,"bootstrap.json"));Pin(GuildStandFiles.PathIn(stand,"config.json"),bootstrap.ConfigurationSha256);
            foreach(var file in stand.Binaries)Pin(GuildStandFiles.PathIn(stand,file.RelativePath),file.Sha256);
            foreach(var check in stand.Prerequisites)Pin(GuildStandFiles.PathIn(stand,check.RelativePath),check.Sha256);
            Pin(GuildStandFiles.PathIn(stand,"profile.pb"));
        }
        catch{Dispose();throw;}
    }
    internal void Pin(string path,string? expected=null)
    {
        AcceptancePaths.File(path);var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            if(expected is not null && Convert.ToHexStringLower(SHA256.HashData(stream))!=expected)throw new InvalidDataException("guild-evidence-byte-mismatch");
            var info=new FileInfo(path);files.Add((path,stream,stream.Length,info.LastWriteTimeUtc));
            string? parent=info.DirectoryName;
            while(parent is not null && !directories.ContainsKey(parent))
            {directories.Add(parent,Directory.GetLastWriteTimeUtc(parent));parent=Path.GetDirectoryName(parent);}
        }
        catch{stream.Dispose();throw;}
    }
    internal void RequireUnchanged()
    {
        foreach(var file in files)
        {
            var current=new FileInfo(file.Path);
            if(file.Stream.SafeFileHandle.IsClosed || !current.Exists || current.Length!=file.Length || current.LastWriteTimeUtc!=file.Written
                || (current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("guild-evidence-drift");
        }
        // Mutable stand parents legitimately acquire world/admission/data files. Source directories remain frozen.
        foreach(var directory in directories)
            if(!Directory.Exists(directory.Key) || AcceptancePaths.Within(directory.Key,sourceRoot) && Directory.GetLastWriteTimeUtc(directory.Key)!=directory.Value)throw new InvalidDataException("guild-evidence-parent-missing");
    }
    internal static IEnumerable<string> RelevantFiles(string source)
    {
        foreach(string name in new[]{"Directory.Build.props","Directory.Packages.props","global.json"})yield return name;
        foreach(string top in new[]{"src","mod","scripts","tools"})
        {
            var pending=new Stack<string>();pending.Push(Path.Combine(source,top));
            while(pending.TryPop(out string? next))foreach(string path in Directory.EnumerateFileSystemEntries(next))
            {
                string relative=Path.GetRelativePath(source,path).Replace('\\','/');
                if(Directory.Exists(path))
                {
                    if(Path.GetFileName(path) is "bin" or "obj" or "node_modules" || relative=="mod/Assemblies")continue;
                    if(top=="tools" && relative.Count(c=>c=='/')==1 && relative is not ("tools/Rimlink.OutpostAcceptanceHost" or "tools/Rimlink.GuildAcceptanceHost" or "tools/Rimlink.BridgeTools"))continue;
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("guild-source-link");
                    if(top!="scripts")pending.Push(path);continue;
                }
                bool code=relative.EndsWith(".cs",StringComparison.Ordinal)||relative.EndsWith(".csproj",StringComparison.Ordinal);
                if(top is "mod" or "scripts" || top=="src" && (code || relative.EndsWith(".proto",StringComparison.Ordinal) || relative.EndsWith(".targets",StringComparison.Ordinal)
                    || relative.StartsWith("src/Rimlink.Server/wwwroot/",StringComparison.Ordinal) || relative.StartsWith("src/Rimlink.Server/Application/Outposts/SchemaProfiles/",StringComparison.Ordinal)&&relative.EndsWith(".json",StringComparison.Ordinal))
                    || top=="tools"&&code)yield return relative;
            }
        }
    }
    public void Dispose(){foreach(var file in files)file.Stream.Dispose();files.Clear();}
}
