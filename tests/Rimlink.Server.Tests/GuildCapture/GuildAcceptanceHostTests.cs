using Rimlink.GuildAcceptanceHost;
using Xunit;
using System.Text;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildAcceptanceHostTests
{
    [Theory]
    [InlineData("../data")][InlineData("data/../anchor")][InlineData("/data")][InlineData("data\\outside")][InlineData("C:/data")][InlineData("")]
    public void Bootstrap_file_names_cannot_escape_the_owned_stand(string name)
    { Assert.Throws<InvalidDataException>(()=>GuildStandFiles.RelativeFile(name)); }

    [Fact]
    public void Distinct_test_identities_and_all_HOME_prerequisites_are_mandatory()
    {
        var names=GuildStandFiles.HomeCases;
        Assert.Equal(6,names.Length);Assert.Equal(names.Length,names.Distinct().Count());
        Assert.DoesNotContain("native-home",names);
        var options=new GuildStandRequest(1,Guid.NewGuid().ToString("D"),"root","source",29991,"executor",new string('a',64),"attacker","defender",[],[],[]);
        GuildStandFiles.ValidateRequest(options);
        Assert.Throws<InvalidDataException>(()=>GuildStandFiles.ValidateRequest(options with {DefenderLogin="attacker"}));
        Assert.Throws<InvalidDataException>(()=>GuildStandFiles.ValidateRequest(options with {Port=0}));
        Assert.Throws<InvalidDataException>(()=>GuildStandFiles.ValidateRequest(options with {ProfileDigest="bad"}));
    }
    [Fact]
    public void Existing_data_and_imported_save_are_refused_before_creating_bootstrap_or_changing_foreign_files()
    {
        using var f=new Staging();Directory.CreateDirectory(Path.Combine(f.Stand.Root,"data"));
        string foreign=Path.Combine(f.Stand.Root,"data","foreign.txt");File.WriteAllText(foreign,"keep",Encoding.UTF8);
        Assert.Throws<InvalidDataException>(()=>GuildStandProgram.Prepare(f.Stand));
        Assert.Equal("keep",File.ReadAllText(foreign));Assert.False(File.Exists(Path.Combine(f.Stand.Root,"bootstrap.json")));
        Directory.Delete(Path.Combine(f.Stand.Root,"data"),true);
        File.WriteAllText(Path.Combine(f.Stand.Root,"saves-attacker","import.rws"),"foreign",Encoding.UTF8);
        Assert.Throws<InvalidDataException>(()=>GuildStandProgram.Prepare(f.Stand));
        Assert.False(File.Exists(Path.Combine(f.Stand.Root,"bootstrap.json")));Assert.False(Directory.Exists(Path.Combine(f.Stand.Root,"data")));
    }
    [Theory]
    [InlineData("missing")][InlineData("duplicate")][InlineData("source")][InlineData("bytes")][InlineData("outcome")][InlineData("path")]
    public void Evidence_must_cover_every_required_case_with_exact_source_and_raw_bytes(string fault)
    {
        using var f=new Staging();string source=new('a',64);string path="evidence/unit-fixture.txt";
        File.WriteAllText(Path.Combine(f.Stand.Root,path),"unit fixture, not native acceptance",Encoding.UTF8);
        string hash=GuildStandFiles.Hash(Path.Combine(f.Stand.Root,path));
        var checks=GuildStandFiles.HomeCases.Select(name=>new GuildStandCheck(name,source,"PASS",path,hash)).ToArray();
        GuildStandFiles.Checks(f.Stand,checks,GuildStandFiles.HomeCases,source);
        checks=fault switch
        {
            "missing"=>checks[1..],"duplicate"=>[checks[0],..checks[..^1]],
            _=>[checks[0] with{SourceIdentity=fault=="source"?new('b',64):source,Sha256=fault=="bytes"?new('0',64):hash,
                Outcome=fault=="outcome"?"NOT RUN":"PASS",RelativePath=fault=="path"?"../unit-fixture.txt":path},..checks[1..]]
        };
        Assert.Throws<InvalidDataException>(()=>GuildStandFiles.Checks(f.Stand,checks,GuildStandFiles.HomeCases,source));
    }
    [Fact]
    public void Host_sources_are_in_the_actual_build_source_inventory()
    {
        string directory=Path.GetDirectoryName(SourceFile())!;
        string checkout=Path.GetFullPath(Path.Combine(directory,"../../.."));
        using var stream=typeof(OutpostFeature).Assembly.GetManifestResourceStream("Rimlink.Outposts.SafetySources")!;
        using var reader=new StreamReader(stream,Encoding.UTF8,true);
        var paths=reader.ReadToEnd().Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Split('|')[0]).Order(StringComparer.Ordinal);
        Assert.Equal(paths,GuildStandEvidence.RelevantFiles(checkout).Order(StringComparer.Ordinal));
        Assert.Contains("tools/Rimlink.GuildAcceptanceHost/GuildStandProgram.cs",paths);
    }
    [Theory]
    [InlineData("Config/Prefs.xml",true)][InlineData("HardwareProfile/gpu-profile.bin",true)][InlineData("ShaderCache/engine.layout-cache",true)]
    [InlineData("TextureCache/v1/cache.zip",true)][InlineData("TextureCache/installs/0011223344556677",true)]
    [InlineData("Saves/home.rws",false)][InlineData("Rimlink/outbox.json",false)][InlineData("ShaderCache/home.rws",false)][InlineData("Config/unknown.xml",false)]
    public void Engine_only_preflight_caches_do_not_admit_colonies_or_unknown_operational_state(string path,bool expected)
    {Assert.Equal(expected,GuildStandFiles.InitialSaveDataFile(path));}
    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath]string path="")=>path;
    private sealed class Staging:IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"rimlink-guild-host-tests-"+Guid.NewGuid().ToString("N"));
        internal GuildStandRequest Stand{get;}
        internal Staging()
        {
            string stand=Path.Combine(root,"stand"),source=Path.Combine(root,"source");Directory.CreateDirectory(source);Directory.CreateDirectory(stand);
            foreach(string name in new[]{"game","saves-executor","saves-attacker","saves-defender","evidence"})Directory.CreateDirectory(Path.Combine(stand,name));
            File.WriteAllText(Path.Combine(stand,"game",OperatingSystem.IsWindows()?"RimWorld.exe":"RimWorldLinux"),"unit placeholder",Encoding.UTF8);
            string[] excluded=Enumerable.Range(1,3).Select(n=>Path.Combine(root,"excluded-"+n)).ToArray();foreach(string path in excluded)Directory.CreateDirectory(path);
            Stand=new(1,Guid.NewGuid().ToString("D"),stand,source,29991,"executor",new('a',64),"attacker","defender",[],[],excluded);
        }
        public void Dispose()=>Directory.Delete(root,true);
    }
}
